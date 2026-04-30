using System.Collections;
using System.Collections.Generic;
using System.Text;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Newtonsoft.Json;

/// <summary>
/// CameraNavigator
/// - Sends a user query to an AI endpoint
/// - Receives a tag name in the response
/// - Finds a GameObject with that tag
/// - Smoothly flies the camera to focus on it
/// </summary>
public class CameraNavigator : MonoBehaviour
{
    [Header("UI References")]
    [Tooltip("InputField where the user types their query")]
    public TMP_InputField queryInputField;

    [Tooltip("Button that triggers the search")]
    public Button searchButton;

    [Tooltip("Optional status label (shows 'Searching…', errors, etc.)")]
    public TMP_Text statusLabel;

    // ---------------------------------------------------------------
    [Header("Endpoint Settings")]
    [Tooltip("Full URL of  AI/tag endpoint")]
    public string endpointURL = "http://127.0.0.1:8000/command";

    // ---------------------------------------------------------------
    [Header("Camera Movement")]
    [Tooltip("How far away from the target the camera stops")]
    public float stopDistance = 5f;

    [Tooltip("How high above the target the camera hovers")]
    public float heightOffset = 2f;

    [Tooltip("Seconds to reach the target (lower = faster)")]
    [Range(0.5f, 10f)]
    public float travelDuration = 3f;

    [Tooltip("Look-at smoothing speed")]
    [Range(1f, 20f)]
    public float rotationSpeed = 5f;

    [Header("Orbit Controls")]
    public float autoOrbitSpeed = 30f;   // degrees per second
    public float orbitMinY = -20f;
    public float orbitMaxY = 80f;

    private Transform orbitTarget;
    private float orbitYaw;
    private float orbitPitch = 15f;      // fixed vertical angle
    private bool isOrbiting = false;

    [Header("Scene Object Settings")]
    [Tooltip("Only include root-level objects (cleaner list for simple scenes)")]
    public bool rootObjectsOnly = false;

    [Tooltip("Tags to exclude from scene graph (e.g. UI, EditorOnly)")]
    public string[] includeTags = { "Player" };

    // ---------------------------------------------------------------
    // Private state
    private Camera cam;
    private bool isTravelling = false;

    // ===============================================================
    void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam == null)
            cam = Camera.main;

        if (searchButton != null)
            searchButton.onClick.AddListener(OnSearchButtonClicked);

        SetStatus("Enter a query and press Search.");
    }

    void Update()
    {
        if (!isOrbiting || orbitTarget == null) return;

        // Continuously increment yaw — no input needed
        orbitYaw += autoOrbitSpeed * Time.deltaTime;

        Quaternion rotation   = Quaternion.Euler(orbitPitch, orbitYaw, 0f);
        Vector3 orbitPosition = orbitTarget.position
                            + rotation * new Vector3(0f, 0f, -stopDistance)
                            + Vector3.up * heightOffset;

        cam.transform.position = orbitPosition;
        cam.transform.LookAt(orbitTarget.position + Vector3.up * (heightOffset * 0.5f));
    }

    // ---------------------------------------------------------------
    /// Called when the Search button is pressed
    public void OnSearchButtonClicked()
    {
        if (isTravelling)
        {
            SetStatus("Already travelling – please wait.");
            return;
        }

        string query = queryInputField != null ? queryInputField.text.Trim() : "";

        if (string.IsNullOrEmpty(query))
        {
            SetStatus("Please enter a query first.");
            return;
        }

        StartCoroutine(QueryEndpointAndNavigate(query));
    }

    // ---------------------------------------------------------------
    /// Sends query → gets tag → finds object → moves camera
    private IEnumerator QueryEndpointAndNavigate(string query)
    {
        SetStatus("Scanning scene…");
        SetButtonInteractable(false);

        // ----- 1. Collect all scene object names -----
        string objList = GetSceneObjectNames();
        Debug.Log($"[CameraNavigator] Scene objects sent: {objList}");

        // ----- 2. Build GET request with query params -----
        string url = $"{endpointURL}?query={UnityEngine.Networking.UnityWebRequest.EscapeURL(query)}&obj={UnityEngine.Networking.UnityWebRequest.EscapeURL(objList)}";

        SetStatus("Sending query…");

        using (UnityEngine.Networking.UnityWebRequest request =
            UnityEngine.Networking.UnityWebRequest.Get(url))
        {
            request.SetRequestHeader("Content-Type", "application/json");
            yield return request.SendWebRequest();

            // ----- 3. Handle network error -----
            if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                SetStatus($"Network error: {request.error}");
                SetButtonInteractable(true);
                yield break;
            }

            // ----- 4. Parse response -----
            string responseText = request.downloadHandler.text;
            Debug.Log($"[CameraNavigator] Response: {responseText}");

            List<CommandResponse> responses = ParseCommandResponse(responseText);

            foreach(var response in responses)
            {
                if (response == null || string.IsNullOrEmpty(response.target))
                {
                    SetStatus("Could not parse a command from the response.");
                    SetButtonInteractable(true);
                    yield break;
                }

                // ----- 5. Find object by name -----
                SetStatus($"Action: {response.function} → \"{response.target}\"");
                GameObject target = GameObject.Find(response.target);

                if (target == null)
                {
                    SetStatus($"No GameObject found with name \"{response.target}\".");
                    SetButtonInteractable(true);
                    yield break;
                }
                
                // 1. Resolve the component type from string
                Type componentType = Type.GetType($"UnityEngine.{response.target}, UnityEngine.CoreModule") 
                  ?? Type.GetType($"UnityEngine.{response.target}, UnityEngine") 
                  ?? Type.GetType($"UnityEngine.{response.target}, UnityEngine.PhysicsModule")
                  ?? Type.GetType($"UnityEngine.{response.target}, Assembly-CSharp");

                if (componentType == null)
                {
                    SetStatus($"Unknown component: \"{response.target}\".");
                    SetButtonInteractable(true);
                    yield break;
                }

                // 2. Get the component from the GameObject
                Component component = target.GetComponent(componentType);

                if (component == null)
                {
                    SetStatus($"Component \"{response.target}\" not found on \"{target.name}\".");
                    SetButtonInteractable(true);
                    yield break;
                }

                // 3. Find the method by name
                MethodInfo method = componentType.GetMethod(
                    response.function,
                    BindingFlags.Instance | BindingFlags.Public
                );

                if (method == null)
                {
                    SetStatus($"Unknown method: \"{response.function}\" on \"{response.target}\".");
                    SetButtonInteractable(true);
                    yield break;
                }

                if (method.Name == "FlyToTarget")
                {
                    FlyToTarget(target.transform);
                    SetButtonInteractable(true);
                    yield break;
                }

                if (method.Name == "StartOrbit")
                {
                    StartOrbit(target);
                    SetButtonInteractable(true);
                    yield break;
                }

                // 4. Match dictionary args to method parameters by name
                ParameterInfo[] parameters = method.GetParameters();
                object[] convertedArgs = new object[parameters.Length];

                for (int i = 0; i < parameters.Length; i++)
                {
                    string paramName = parameters[i].Name;
                    Type paramType = parameters[i].ParameterType;

                    if (!response.args.TryGetValue(paramName, out string value))
                    {
                        SetStatus($"Missing argument \"{paramName}\" for \"{response.function}\".");
                        SetButtonInteractable(true);
                        yield break;
                    }

                    // Convert to the expected type
                    convertedArgs[i] = Convert.ChangeType(value, paramType);
                }

                method.Invoke(component, convertedArgs);
                yield return new WaitForSeconds(0.5f);
                // ----- 6. Dispatch action -----
                // yield return StartCoroutine(DispatchAction(response.action, target));
            }
            
        }

        SetButtonInteractable(true);
    }

    // ---------------------------------------------------------------
    /// Collects all GameObject names from the active scene
    private string GetSceneObjectNames()
    {
        System.Collections.Generic.List<string> names = new System.Collections.Generic.List<string>();

        if (rootObjectsOnly)
        {
            // Root objects only
            UnityEngine.SceneManagement.Scene scene =
                UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            foreach (GameObject go in scene.GetRootGameObjects())
            {
                if (IsIncluded(go))
                    names.Add(go.name);
            }
        }
        else
        {
            // Every object in the scene (including children)
            foreach (GameObject go in FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            {
                if (HasIncludedAncestor(go))
                    names.Add(go.name);
            }
        }

        return string.Join(",", names);
    }

    /// Returns true if the object should be skipped
    private bool IsIncluded(GameObject go)
    {
        foreach (string includeTag in includeTags)
        {
            if (!string.IsNullOrEmpty(includeTag) && go.CompareTag(includeTag))
                return true;
        }
        return false;
    }

    private bool HasIncludedAncestor(GameObject go)
    {
        Transform parent = go.transform.parent;

        while (parent != null)
        {
            if (IsIncluded(parent.gameObject))
                return true;

            parent = parent.parent;
        }

        return false;
    }

    // ---------------------------------------------------------------
    /// Parses { "action": "highlight", "object": "wheel_front_left" }
    private List<CommandResponse> ParseCommandResponse(string json)
    {
        try
        {
            // return JsonUtility.FromJson<CommandResponse>(json);
            return JsonConvert.DeserializeObject<List<CommandResponse>>(json);
        }
        catch
        {
            Debug.LogWarning($"[CameraNavigator] Failed to parse: {json}");
            return null;
        }
    }

    // ---------------------------------------------------------------
    /// Routes the action string to the correct behaviour
    private IEnumerator DispatchAction(string action, GameObject target)
    {
        // Stop any existing orbit before acting
        StopOrbit();

        switch (action.ToLower())
        {
            case "highlight":
                HighlightObject(target);
                SetStatus($"Highlighted \"{target.name}\".");
                break;

            case "navigate":
            case "focus":
                yield return StartCoroutine(FlyToTarget(target.transform));
                SetStatus($"Arrived at \"{target.name}\".");
                break;
            
            case "move_camera":
            case "orbit":
                // Fly to target first, then begin orbiting
                yield return StartCoroutine(FlyToTarget(target.transform));
                StartOrbit(target);
                break;

            case "highlight_and_navigate":
                HighlightObject(target);
                yield return StartCoroutine(FlyToTarget(target.transform));
                SetStatus($"Highlighted and arrived at \"{target.name}\".");
                break;

            case "highlight_and_orbit":
                HighlightObject(target);
                yield return StartCoroutine(FlyToTarget(target.transform));
                StartOrbit(target);
                break;

            default:
                Debug.LogWarning($"[CameraNavigator] Unknown action: {action}");
                SetStatus($"Unknown action \"{action}\" for \"{target.name}\".");
                break;
        }
    }

    // ---------------------------------------------------------------
    /// Simple highlight: swaps all Renderer materials to a highlight color.
    /// Stores originals so can call ClearHighlights() to restore them.
    [Header("Highlight Settings")]
    public Color highlightColor = Color.yellow;

    private System.Collections.Generic.Dictionary<Renderer, Material[]> originalMaterials
        = new System.Collections.Generic.Dictionary<Renderer, Material[]>();

    private void HighlightObject(GameObject target)
    {
        ClearHighlights(); // Remove any previous highlight

        foreach (Renderer rend in target.GetComponentsInChildren<Renderer>())
        {
            // Store originals
            originalMaterials[rend] = rend.materials;

            // Apply highlight to every material slot
            Material[] highlighted = new Material[rend.materials.Length];
            for (int i = 0; i < highlighted.Length; i++)
            {
                highlighted[i] = new Material(rend.materials[i]);
                highlighted[i].color = highlightColor;
            }
            rend.materials = highlighted;
        }
    }

    public void ClearHighlights()
    {
        foreach (var kvp in originalMaterials)
        {
            if (kvp.Key != null)
                kvp.Key.materials = kvp.Value;
        }
        originalMaterials.Clear();
    }

    // ---------------------------------------------------------------
    // Serialisable response class
    [System.Serializable]
    public class CommandResponse
    {
        public string function;
        public string target;
        public Dictionary<string, string> args;
        public string instruction;
        public int priority;
        public float confidence;
    }

    // ---------------------------------------------------------------
    /// Smoothly moves the camera to a position in front of the target
    private IEnumerator FlyToTarget(Transform target)
    {
        isTravelling = true;

        Vector3 startPos  = cam.transform.position;
        Quaternion startRot = cam.transform.rotation;

        // Destination: behind-and-above the target relative to world forward
        Vector3 direction    = (cam.transform.position - target.position).normalized;
        Vector3 destination  = target.position
                               + direction * stopDistance
                               + Vector3.up * heightOffset;

        Quaternion targetRot = Quaternion.LookRotation(
            (target.position - destination).normalized);

        float elapsed = 0f;

        while (elapsed < travelDuration)
        {
            elapsed += Time.deltaTime;
            float t  = Mathf.SmoothStep(0f, 1f, elapsed / travelDuration);

            cam.transform.position = Vector3.Lerp(startPos, destination, t);
            cam.transform.rotation = Quaternion.Slerp(startRot, targetRot, t);

            yield return null;
        }

        // Ensure exact final values
        cam.transform.position = destination;
        cam.transform.rotation = targetRot;

        isTravelling = false;
    }

    private void StartOrbit(GameObject target)
    {
        orbitTarget = target.transform;

        // Start yaw from where the camera already is — no jarring snap
        orbitYaw   = cam.transform.eulerAngles.y;
        orbitPitch = 15f;  // consistent elevated angle

        isOrbiting = true;
        SetStatus($"Orbiting \"{target.name}\"…");
    }

    public void StopOrbit()
    {
        isOrbiting  = false;
        orbitTarget = null;
    }

    // ---------------------------------------------------------------
    // ---- Helpers ---------------------------------------------------

    /// <summary>
    /// Parses the tag string out of the JSON response.
    /// Expected format:  { "tag": "Enemy" }
    /// Adjust if endpoint returns a different schema.
    /// </summary>
    private string ParseTagFromResponse(string json)
    {
        try
        {
            TagResponse parsed = JsonUtility.FromJson<TagResponse>(json);
            return parsed?.tag?.Trim();
        }
        catch
        {
            Debug.LogWarning($"[CameraNavigator] Could not parse response: {json}");
            return null;
        }
    }

    private void SetStatus(string msg)
    {
        if (statusLabel != null)
            statusLabel.text = msg;
        Debug.Log($"[CameraNavigator] {msg}");
    }

    private void SetButtonInteractable(bool state)
    {
        if (searchButton != null)
            searchButton.interactable = state;
    }

    // ---------------------------------------------------------------
    // ---- Serialisable helper classes --------------------------------

    [System.Serializable]
    private class QueryPayload
    {
        public string query;
    }

    [System.Serializable]
    private class TagResponse
    {
        public string tag;
    }
}