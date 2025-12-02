using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public class AIServerInterface
{
    private ThalmicMyo thalmicMyo;
    private int memorySize = 6;
    private string currentGesture = "Unknown";
    private string currentGestureProb = "Uncertain";
    private Queue<PredictionResponse> previousGesture = new Queue<PredictionResponse>();

    // Batch windowed prediction settings
    private string sessionId;
    private int batchSize = 10;  // Collect 10 samples before sending
    private List<int[]> emgBatch = new List<int[]>();
    private bool isProcessingRequest = false;
    private bool isBuffering = true;

    public AIServerInterface(ThalmicMyo myo)
    {
        thalmicMyo = myo;
        // Generate unique session ID for this Unity instance
        sessionId = $"unity_{System.Guid.NewGuid().ToString()}";
        Debug.Log($"[AIServerInterface] Session ID: {sessionId}");
    }

    public void StartPredictionRequestCoroutine()
    {
        // Get current EMG sample
        int[] currentEmgData = new int[8];
        System.Array.Copy(thalmicMyo._myoEmg, currentEmgData, 8);
        
        // Add to batch
        emgBatch.Add(currentEmgData);

        // Send batch when it reaches the batch size
        if (emgBatch.Count >= batchSize && !isProcessingRequest)
        {
            _ = SendBatchWindowedPredictionRequest(new List<int[]>(emgBatch));
            emgBatch.Clear();
        }
    }

    private async Task SendBatchWindowedPredictionRequest(List<int[]> batch)
    {
        if (isProcessingRequest) return;
        isProcessingRequest = true;

        // Build batch JSON - samples in chronological order
        List<string> batchSamples = new List<string>();
        foreach (int[] emgs in batch)
        {
            string sample = $@"{{
                ""EMG1"": {emgs[0]},
                ""EMG2"": {emgs[1]},
                ""EMG3"": {emgs[2]},
                ""EMG4"": {emgs[3]},
                ""EMG5"": {emgs[4]},
                ""EMG6"": {emgs[5]},
                ""EMG7"": {emgs[6]},
                ""EMG8"": {emgs[7]}
            }}";
            batchSamples.Add(sample);
        }
        
        string json = $@"{{
            ""batch"": [{string.Join(",", batchSamples)}],
            ""session_id"": ""{sessionId}""
        }}";

        try
        {
            using (UnityWebRequest www = new UnityWebRequest("http://127.0.0.1:8000/batch_predict_windowed", "POST"))
            {
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
                www.uploadHandler = new UploadHandlerRaw(bodyRaw);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                www.timeout = 10;

                UnityWebRequestAsyncOperation operation = www.SendWebRequest();
                while (!operation.isDone)
                    await Task.Yield();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("[AIServerInterface] Batch windowed prediction error: " + www.error);
                }
                else
                {
                    string responseText = www.downloadHandler.text;
                    BatchWindowedResponse result = null;
                    try
                    {
                        result = JsonUtility.FromJson<BatchWindowedResponse>(responseText);
                    }
                    catch
                    {
                        Debug.LogWarning("[AIServerInterface] Failed to parse batch windowed response: " + responseText);
                    }

                    if (result != null && result.predictions != null)
                    {
                        ProcessBatchWindowedResponse(result);
                    }
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[AIServerInterface] Exception during batch windowed prediction: " + e.Message);
        }
        finally
        {
            isProcessingRequest = false;
        }
    }

    private void ProcessBatchWindowedResponse(BatchWindowedResponse response)
    {
        if (response.predictions == null || response.predictions.Count == 0)
        {
            return;
        }

        // Process predictions in order, only use the most recent predicted samples
        List<PredictionItem> predictedSamples = response.predictions
            .Where(p => p.status == "predicted")
            .ToList();

        if (predictedSamples.Count == 0)
        {
            // All samples still buffering
            PredictionItem lastPrediction = response.predictions[response.predictions.Count - 1];
            if (lastPrediction.status == "buffering")
            {
                isBuffering = true;
                currentGesture = "Neutral";
                currentGestureProb = "Buffering";
                
                // Log buffering progress occasionally
                if (lastPrediction.buffer_size % 5 == 0)
                {
                    Debug.Log($"[AIServerInterface] Buffering... {lastPrediction.buffer_size} samples collected");
                }
            }
            return;
        }

        if (isBuffering)
        {
            isBuffering = false;
            Debug.Log("[AIServerInterface] Buffer filled, predictions active");
        }

        // Use only the most recent predictions for gesture stability
        // Take up to last 3 predictions from the batch to update memory
        int predictionsToUse = Mathf.Min(3, predictedSamples.Count);
        for (int i = predictedSamples.Count - predictionsToUse; i < predictedSamples.Count; i++)
        {
            PredictionItem prediction = predictedSamples[i];
            
            PredictionResponse predResponse = new PredictionResponse
            {
                label = prediction.label,
                prob = prediction.prob
            };

            // Update memory queue
            if (previousGesture.Count >= memorySize)
            {
                previousGesture.Dequeue();
            }
            previousGesture.Enqueue(predResponse);
        }

        // Majority voting: gesture must appear in >50% of last N predictions
        if (previousGesture.Count > 0)
        {
            // Get most common gesture in memory
            IGrouping<string, PredictionResponse> mostCommon = previousGesture
                .GroupBy(p => p.label)
                .OrderByDescending(g => g.Count())
                .First();

            if (previousGesture.Count(p => p.label == mostCommon.Key) >= memorySize / 2)
            {
                currentGesture = mostCommon.Key;
                currentGestureProb = mostCommon.Average(p => p.prob).ToString("F2");
            }
            else
            {
                currentGesture = "Unknown";
                currentGestureProb = "Uncertain";
            }
        }
    }

    public string GetCurrentGesture() => currentGesture;
    public string GetCurrentGestureProb() => currentGestureProb;
    public bool IsBuffering() => isBuffering;

    /// <summary>
    /// Clear the server-side buffer for this session (useful for resetting)
    /// </summary>
    public async Task ClearSession()
    {
        try
        {
            string url = $"http://127.0.0.1:8000/clear_session?session_id={sessionId}";
            using (UnityWebRequest www = UnityWebRequest.Post(url, ""))
            {
                UnityWebRequestAsyncOperation operation = www.SendWebRequest();
                while (!operation.isDone)
                    await Task.Yield();

                if (www.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log("[AIServerInterface] Session cleared successfully");
                    isBuffering = true;
                    previousGesture.Clear();
                    currentGesture = "Unknown";
                    currentGestureProb = "Uncertain";
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[AIServerInterface] Failed to clear session: " + e.Message);
        }
    }

    // Helper classes for JSON parsing
    [System.Serializable]
    private class PredictionResponse
    {
        public string label;
        public float prob;
    }

    [System.Serializable]
    private class PredictionItem
    {
        public string status;           // "buffering" or "predicted"
        public int sample_index;        // Index in the batch
        public int samples_needed;      // (buffering only)
        public int buffer_size;         // (buffering only)
        public string label;            // (predicted only)
        public float prob;              // (predicted only)
        public List<TopKItem> topk;     // (predicted only)
    }

    [System.Serializable]
    private class BatchWindowedResponse
    {
        public string session_id;
        public int total_samples;
        public List<PredictionItem> predictions;
        public bool buffer_ready;
    }

    [System.Serializable]
    private class TopKItem
    {
        public string label;
        public float prob;
    }
}
