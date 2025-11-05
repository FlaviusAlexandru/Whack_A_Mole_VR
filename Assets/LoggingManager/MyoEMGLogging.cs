using System.Collections.Generic;
using System.Linq;
using Thalmic.Myo;
using UnityEngine;

public class MyoEMGLogging : MonoBehaviour
{
    [SerializeField] private LoggingManager loggingManager;
    [SerializeField] private EMGPointer rightHandEMGPointer;
    [SerializeField] public ThalmicMyo thalmicMyo;

    List<string> EMGCol;
    private bool isLoggingStarted = false;

    void Awake()
    {
        // Auto-subscribe to GameDirector state updates so logging starts/stops with the game
        GameDirector director = FindObjectOfType<GameDirector>();
        if (director != null)
        {
            director.stateUpdate.AddListener(OnGameDirectorStateUpdate);
        }
        else
        {
            Debug.LogWarning("MyoEMGLogging: GameDirector not found. EMG logging won't start automatically.");
        }
    }

    void OnDisable()
    {
        // Unsubscribe to avoid leaks and make sure we stop logging
        GameDirector director = FindObjectOfType<GameDirector>();
        if (director != null)
        {
            director.stateUpdate.RemoveListener(OnGameDirectorStateUpdate);
        }
        FinishLogging();
    }

    void Start()
    {
        // Define EMG column headers and additional log columns.
        EMGCol = new List<string> { "EMG1", "EMG2", "EMG3", "EMG4", "EMG5", "EMG6", "EMG7", "EMG8" };
        List<string> logCols = new List<string>(EMGCol)
        {
            "CurrentGestures",
            "Threshold",
            "PredictionConfidence"
        };

        // Initialize EMG log collection with specified columns.
        if (loggingManager == null)
        {
            loggingManager = FindObjectOfType<LoggingManager>();
            if (loggingManager == null)
            {
                Debug.LogError("MyoEMGLogging: LoggingManager not set and not found in scene. CSV output will not work.");
            }
        }
        else
        {
            loggingManager.CreateLog("EMG", logCols);
        }

        // If LoggingManager was found via FindObjectOfType, still create the log
        if (loggingManager != null)
        {
            loggingManager.CreateLog("EMG", logCols);
        }
    }

    public void OnGameDirectorStateUpdate(GameDirector.GameState newState)
    {
        switch (newState)
        {
            case GameDirector.GameState.Stopped:
                FinishLogging();
                break;
            case GameDirector.GameState.Playing:
                StartLogging();
                break;
            case GameDirector.GameState.Paused:
                // TODO
                break;
        }
    }

    private void StartLogging()
    {
        if (isLoggingStarted) return;

        if (thalmicMyo == null || thalmicMyo._myo == null)
        {
            Debug.LogWarning("MyoEMGLogging: ThalmicMyo reference is missing. Cannot subscribe to EMG data.");
            return;
        }

        // Add event handlers to the Myo device to receive EMG data.
        thalmicMyo._myo.EmgData += onReceiveData;

        isLoggingStarted = true;
    }

    private void onReceiveData(object sender, EmgDataEventArgs data)
    {
        if (loggingManager == null)
        {
            // If not set for any reason, try to recover once
            loggingManager = FindObjectOfType<LoggingManager>();
            if (loggingManager == null)
            {
                return; // Can't log without a manager
            }
        }

        // Format the EMG data into a dictionary {"EMG_i", data.Emg[i]}
        Dictionary<string, object> emgData = EMGCol
            .Select((col, i) => new { col, value = data.Emg[i] })
            .ToDictionary(x => x.col, x => (object)x.value);

        // Add CurrentGestures and Threshold columns with their current values (guard against null pointer)
        string gesture = rightHandEMGPointer != null ? rightHandEMGPointer.GetCurrentGesture().ToString() : "Unknown";
        string threshold = rightHandEMGPointer != null ? rightHandEMGPointer.getThresholdState() : "Unknown";
        string conf = rightHandEMGPointer != null ? rightHandEMGPointer.GetCurrentGestureConfidence() : "Uncertain";
        emgData["CurrentGestures"] = gesture;
        emgData["Threshold"] = threshold;
        emgData["PredictionConfidence"] = conf;

        // Time.frameCount (used in LogStore) can only be accessed from the main
        // thread so we use MainThreadDispatcher to enqueue the logging action.
        MainThreadDispatcher.Enqueue(() =>
        {
            loggingManager.Log("EMG", emgData);
        });
    }

    void FinishLogging()
    {
        if (thalmicMyo != null && thalmicMyo._myo != null)
        {
            thalmicMyo._myo.EmgData -= onReceiveData;
        }
        isLoggingStarted = false;
    }
}
