using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

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
    [Tooltip("Full URL of your AI/tag endpoint")]
    public string endpointURL = "https://your-endpoint.com/api/get-tag";

    // ---------------------------------------------------------------
    [Header("Camera Movement")]
    [Tooltip("How far away from the target the camera stops")]
    public float stopDistance = 5f;

    [Tooltip("How high above the target the camera hovers")]
    public float heightOffset = 2f;

    [Tooltip("Seconds to reach the target (lower = faster)")]
    [Range(0.5f, 10f)]
    public float travelDuration = 2f;

    [Tooltip("Look-at smoothing speed")]
    [Range(1f, 20f)]
    public float rotationSpeed = 5f;

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
        SetStatus("Sending query…");
        SetButtonInteractable(false);

        // ----- 1. Build request -----
        string jsonBody = JsonUtility.ToJson(new QueryPayload { query = query });
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);

        using (UnityEngine.Networking.UnityWebRequest request =
               new UnityEngine.Networking.UnityWebRequest(endpointURL, "POST"))
        {
            request.uploadHandler   = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            // ----- 2. Handle network error -----
            if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                SetStatus($"Network error: {request.error}");
                SetButtonInteractable(true);
                yield break;
            }

            // ----- 3. Parse response -----
            string responseText = request.downloadHandler.text;
            string tag = ParseTagFromResponse(responseText);

            if (string.IsNullOrEmpty(tag))
            {
                SetStatus("Could not read a tag from the response.");
                SetButtonInteractable(true);
                yield break;
            }

            // ----- 4. Find object by tag -----
            SetStatus($"Looking for tag: \"{tag}\"…");
            GameObject target = GameObject.FindWithTag(tag);

            if (target == null)
            {
                SetStatus($"No GameObject found with tag \"{tag}\".");
                SetButtonInteractable(true);
                yield break;
            }

            // ----- 5. Fly to target -----
            SetStatus($"Navigating to \"{target.name}\"…");
            yield return StartCoroutine(FlyToTarget(target.transform));
            SetStatus($"Arrived at \"{target.name}\".");
        }

        SetButtonInteractable(true);
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

    // ---------------------------------------------------------------
    // ---- Helpers ---------------------------------------------------

    /// <summary>
    /// Parses the tag string out of the JSON response.
    /// Expected format:  { "tag": "Enemy" }
    /// Adjust if your endpoint returns a different schema.
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