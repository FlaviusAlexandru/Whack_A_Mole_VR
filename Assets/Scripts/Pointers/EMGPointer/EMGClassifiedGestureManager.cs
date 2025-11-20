using System.Collections;
using System.Linq;
using System.Collections.Generic; // Added for List<HandGestureState>
using UnityEngine;
using Valve.VR;


//Enum for hand gesture states
//Each value should correspond exactly (including naming and casing) to the Manual Blending behavior names created in
//SteamVR's Blending Editor
public enum HandGestureState
{
    Neutral,
    PalmarGrasp,
    OpenHand,
    WristExtension,
    WristFlexion,
    LateralGrasp,
    Unknown // Do nothing state, used when gesture is not recognized
}

public class EMGClassifiedGestureManager : MonoBehaviour
{

    // Todo: Confirm gesture changes are reflected in scene at runtime. STILL ONGOING
    // Todo: Integrate with EMG input system to trigger SetPose based on classified gestures.


    private SteamVR_Skeleton_Poser poser; // Reference to the SteamVR_Skeleton_Poser component, drives pose blending at runtime
    private Coroutine currentBlendCoroutine; //Tracks currently running blending coroutine, later used for smooth transitioning
    private SteamVR_Behaviour_Skeleton skeleton;
    [SerializeField]
    [Tooltip("Duration for blending transitions between poses, in seconds")]
    public float blendDuration = 0.3f; //Duration for blending transitions between poses

    // --- Haptics on gesture change ---
    [Header("Haptics")]
    [SerializeField]
    [Tooltip("Connector used to trigger tactor pulses when gesture changes")]
    private TactorConnector tactorConnector;

    [SerializeField]
    [Tooltip("Which tactor to pulse (1-based)")]
    private int hapticTactorNumber = 1;

    [SerializeField]
    [Tooltip("Log gesture change and pulse events to the Console")]
    private bool logGestureHaptics = true;

    [SerializeField]
    [Tooltip("Gestures that should trigger a tactor pulse when they become active. Leave empty for no pulses. 'Unknown' is ignored.")]
    private List<HandGestureState> pulseOnGestures = new List<HandGestureState> { HandGestureState.PalmarGrasp, HandGestureState.LateralGrasp }; // example defaults

    // Tracks last applied gesture to avoid re-pulsing/re-blending every frame
    private HandGestureState lastAppliedGesture = HandGestureState.Unknown;

    private void Awake()
    {
        StartCoroutine(WaitHandInstantiated()); // Start the coroutine to wait for the hand model (with SteamVR_Skeleton_Poser) to spawn, grabs reference once available.
    }

    private void OnValidate()
    {
        // Allow adding entries in inspector: DO NOT recreate list or remove duplicates automatically
        if (pulseOnGestures == null) pulseOnGestures = new List<HandGestureState>();

        // Strip Unknown entries only (leave duplicates so user can change them to different enums)
        for (int i = pulseOnGestures.Count - 1; i >= 0; i--)
        {
            if (pulseOnGestures[i] == HandGestureState.Unknown)
                pulseOnGestures.RemoveAt(i);
        }
    }

    private void Update()
    {
        //For testing purposes, you can change the gesture state using keyboard input at runtime
        if (Input.GetKeyDown(KeyCode.Alpha1)) SetPose(HandGestureState.Neutral);
        else if (Input.GetKeyDown(KeyCode.Alpha2)) SetPose(HandGestureState.PalmarGrasp);
        else if (Input.GetKeyDown(KeyCode.Alpha3)) SetPose(HandGestureState.OpenHand);
        else if (Input.GetKeyDown(KeyCode.Alpha4)) SetPose(HandGestureState.WristExtension);
        else if (Input.GetKeyDown(KeyCode.Alpha5)) SetPose(HandGestureState.WristFlexion);
        else if (Input.GetKeyDown(KeyCode.Alpha6)) SetPose(HandGestureState.LateralGrasp);
    }

    //Triggers a smooth transition to the specified pose
    public void SetPose(HandGestureState gestureState)
    {
        if (gestureState == HandGestureState.Unknown) return; // Ignore requests to set to Unknown state
        if (lastAppliedGesture == gestureState) return; // Avoid redundant work

        if (poser == null)
        {
            Debug.LogWarning($"Attempted to set pose {gestureState}, but poser is not initialized yet.");
            return;
        }

        string target = gestureState.ToString(); // Must match Blending Editor names

        // Fire haptic pulse only if gesture is in the configured list
        TryPulseOnGestureChange(lastAppliedGesture, gestureState);

        if (currentBlendCoroutine != null) StopCoroutine(currentBlendCoroutine);
        currentBlendCoroutine = StartCoroutine(CrossFadePose(target, blendDuration));

        lastAppliedGesture = gestureState;
    }

    private void TryPulseOnGestureChange(HandGestureState from, HandGestureState to)
    {
        // Only pulse if this gesture is configured
        if (pulseOnGestures == null || !pulseOnGestures.Contains(to))
        {
            if (logGestureHaptics)
                Debug.Log($"[Haptics] Gesture change {from} -> {to} (no pulse: not in list).");
            return;
        }

        // Lazy resolve if not assigned
        if (tactorConnector == null) tactorConnector = FindObjectOfType<TactorConnector>();

        if (tactorConnector == null)
        {
            if (logGestureHaptics)
                Debug.LogWarning($"[Haptics] No TactorConnector found to pulse on gesture change {from} -> {to}.");
            return;
        }

        if (!tactorConnector.feedbackEnabled || !tactorConnector.IsConnected())
        {
            if (logGestureHaptics)
                Debug.Log($"[Haptics] Gesture {from} -> {to}, haptics disabled or not connected.");
            return;
        }

        int tactorId = Mathf.Max(1, hapticTactorNumber);
        if (logGestureHaptics)
            Debug.Log($"[Haptics] Gesture change: {from} -> {to}. Pulsing tactor {tactorId}.");
        tactorConnector.ActivateTactor(tactorId);
    }

    //Coroutine to smoothly transition between poses over a specified duration
    private IEnumerator CrossFadePose(string targetPose, float duration)
    {
        string[] behaviors = HandGestureState.GetNames(typeof(HandGestureState))
            .Where(b => b != HandGestureState.Unknown.ToString())
            .ToArray();

        Dictionary<string, float> startValues = behaviors.ToDictionary(b => b, b => poser.GetBlendingBehaviourValue(b));
        Dictionary<string, float> targetValues = behaviors.ToDictionary(b => b, b => (b == targetPose) ? 1f : 0f);
        float time = 0f;
        while (time < duration)
        {
            time += Time.deltaTime;
            float normalizedTime = time / duration;
            foreach (string behavior in behaviors)
            {
                float interpolatedValue = Mathf.Lerp(startValues[behavior], targetValues[behavior], normalizedTime);
                poser.SetBlendingBehaviourValue(behavior, interpolatedValue);
            }
            yield return null;
        }
        foreach (string b in behaviors)
        {
            poser.SetBlendingBehaviourValue(b, (b == targetPose) ? 1f : 0f);
        }
    }

    //Coroutine to wait until the SteamVR_Skeleton_Poser component is available
    private IEnumerator WaitHandInstantiated()
    {
        while (poser == null)
        {
            poser = GetComponentInChildren<SteamVR_Skeleton_Poser>();
            yield return null;
        }

        Debug.Log("SteamVR_Skeleton_Poser component found and reference grabbed.");
        SetPose(HandGestureState.Neutral); // initial pose

        skeleton = GetComponentInChildren<SteamVR_Behaviour_Skeleton>();
        skeleton.BlendToPoser(poser, 0f); // Force immediate switch
        Debug.Log("Forced blend to custom poser.");
    }
}
