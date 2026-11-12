using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Quick rotation of the drone presented in the space (board function: "Quick rotation", right grip).
///
///   Right grip, tap  -> rotate the drone 45 degrees about the current axis
///   Right grip, hold -> switch the rotation axis (Y -> X -> Z -> Y)
///
/// The drone turns about its own centre, so it stays where it is in the space. Cycle the axis,
/// then tap repeatedly to step the model round 45 degrees at a time.
///
/// Multiplayer: the drone's pose is replicated by the ClientNetworkTransform on the prefab, and that
/// only replicates what the object's owner writes. So any player can press the button, but the turn
/// is actually performed by the owner (the request is forwarded to it). The current axis is shared
/// through a NetworkVariable so every player sees the same cycling order.
///
/// Usage: add to the same GameObject as ParametricDrone. Works on any model whose parts are
/// separate child objects (e.g. an imported CAD assembly) too.
///
/// Note: the right grip is also used by some XR rigs for grabbing. If tapping the grip grabs the
/// drone instead of rotating it, rebind Rotate Input to a free button (e.g. the left grip).
/// </summary>
public class DroneRotator : NetworkBehaviour
{
    [Header("Rotation")]
    [Tooltip("Degrees turned per tap.")]
    public float stepDegrees = 45f;
    [Tooltip("Holding the button longer than this switches the rotation axis instead of rotating.")]
    public float longPressSeconds = 0.45f;
    [Tooltip("Axis used first, before the button cycles through the others.")]
    public Axis startAxis = Axis.Y;

    [Header("Input (default: right grip)")]
    public InputActionProperty rotateInput;

    public enum Axis { Y, X, Z }

    // Order the axis cycles through on a hold.
    static readonly Axis[] CycleOrder = { Axis.Y, Axis.X, Axis.Z };

    Axis currentAxis;
    float pressedAt = -1f;
    bool longPressHandled;

    /// <summary>Shared rotation axis. Written by the drone's owner, read by everyone.</summary>
    readonly NetworkVariable<Axis> m_CurrentAxis = new NetworkVariable<Axis>(
        Axis.Y, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    void OnEnable()
    {
        if (rotateInput.action == null || rotateInput.action.bindings.Count == 0)
            rotateInput = new InputActionProperty(new InputAction("Rotate Drone", InputActionType.Button,
                "<XRController>{RightHand}/{GripButton}"));
        rotateInput.action.Enable();

        currentAxis = startAxis;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_CurrentAxis.OnValueChanged += OnAxisChanged;

        if (IsOwner) m_CurrentAxis.Value = startAxis;    // publish the Inspector value
        else currentAxis = m_CurrentAxis.Value;          // catch up with the shared value
    }

    public override void OnNetworkDespawn()
    {
        m_CurrentAxis.OnValueChanged -= OnAxisChanged;
        base.OnNetworkDespawn();
    }

    void OnAxisChanged(Axis previous, Axis current) => currentAxis = current;

    void Update()
    {
        if (rotateInput.action.WasPressedThisFrame())
        {
            pressedAt = Time.time;
            longPressHandled = false;
        }

        // Held past the threshold -> cycle the axis, once per press
        if (!longPressHandled && pressedAt >= 0f && Time.time - pressedAt >= longPressSeconds)
        {
            longPressHandled = true;
            CycleAxis();
        }

        // Released before the threshold -> a normal rotate
        if (rotateInput.action.WasReleasedThisFrame())
        {
            if (!longPressHandled && pressedAt >= 0f) Rotate();
            pressedAt = -1f;
        }
    }

    /// <summary>Rotate one step about the current axis. Also callable from a UI button.</summary>
    public void Rotate()
    {
        // Only the owner's transform writes get replicated, so a remote press is forwarded to it.
        if (!IsSpawned || IsOwner) RotateLocal();
        else RequestRotateRpc();
    }

    [Rpc(SendTo.Owner)]
    void RequestRotateRpc() => RotateLocal();

    void RotateLocal()
    {
        Vector3 localAxis;
        switch (currentAxis)
        {
            case Axis.X: localAxis = Vector3.right;   break;
            case Axis.Z: localAxis = Vector3.forward; break;
            default:     localAxis = Vector3.up;      break;   // Y
        }

        // Turn about the drone's own axis, through its own centre, so it spins in place.
        Vector3 worldAxis = transform.TransformDirection(localAxis);
        transform.RotateAround(Centre(), worldAxis, stepDegrees);
        Debug.Log($"Rotated {stepDegrees} deg about {currentAxis}");
    }

    /// <summary>Advance to the next rotation axis. Also callable from a UI button.</summary>
    public void CycleAxis()
    {
        if (!IsSpawned || IsOwner) CycleAxisLocal();
        else RequestCycleAxisRpc();
    }

    [Rpc(SendTo.Owner)]
    void RequestCycleAxisRpc() => CycleAxisLocal();

    void CycleAxisLocal()
    {
        int i = System.Array.IndexOf(CycleOrder, currentAxis);
        Axis next = CycleOrder[(i + 1) % CycleOrder.Length];

        if (!IsSpawned) currentAxis = next;              // no network running
        else m_CurrentAxis.Value = next;                 // owner publishes; OnAxisChanged updates locally too

        Debug.Log($"Rotation axis: {next}");
    }

    /// <summary>World-space centre of the whole model, used as the pivot.</summary>
    Vector3 Centre()
    {
        var renderers = GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return transform.position;

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b.center;
    }
}
