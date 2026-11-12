using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Exploded view (board function: "Exploded view", B button).
///
/// Method from Freeman (2017): find the centre of the whole assembly, then move each
/// part outward along the line from that centre to the part's own centre. Parts that
/// are further from the centre move further, so stacked parts (motor, prop) separate.
///
///   B or Y button (secondary, either controller) -> explode / collapse (animated)
///   Laptop simulator: 2 (right) or Shift+2 (left)
///
/// Multiplayer: the exploded/collapsed state is shared through a NetworkVariable, so every
/// player sees the same view. Any player may press the button; the request is forwarded to the
/// drone's owner, who publishes the new state. The animation itself (the "factor") is played
/// locally on each client, so no geometry is sent over the network.
///
/// Usage: add to the same GameObject as ParametricDrone. Works on any model whose
/// parts are separate child objects (e.g. an imported CAD assembly) too.
/// </summary>
public class ExplodedView : NetworkBehaviour
{
    [Tooltip("How far parts move apart. 1 = double their distance from the centre.")]
    [Range(0.2f, 4f)] public float spread = 1.5f;
    [Tooltip("Animation speed (full explode in 1/speed seconds).")]
    public float speed = 2f;

    [Header("Input (default: B or Y button)")]
    public InputActionProperty toggleInput;

    public bool exploded;

    class Part { public Transform t; public Vector3 basePos; public Vector3 offset; }
    readonly List<Part> parts = new List<Part>();
    float factor;           // 0 = assembled, 1 = fully exploded
    bool needCapture;       // scheduled a Capture once the drone geometry exists
    ParametricDrone drone;

    /// <summary>Shared exploded state. Written by the drone's owner, read by everyone.</summary>
    readonly NetworkVariable<bool> m_Exploded = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    void OnEnable()
    {
        if (toggleInput.action == null || toggleInput.action.bindings.Count == 0)
        {
            var action = new InputAction("Explode", InputActionType.Button,
                "<XRController>{RightHand}/{SecondaryButton}");
            action.AddBinding("<XRController>{LeftHand}/{SecondaryButton}");
            toggleInput = new InputActionProperty(action);
        }
        toggleInput.action.Enable();

        // If the drone is rebuilt (a dimension changed) re-measure the parts
        drone = GetComponent<ParametricDrone>();
        if (drone != null) drone.Rebuilt += Capture;
    }

    void OnDisable()
    {
        if (drone != null) drone.Rebuilt -= Capture;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_Exploded.OnValueChanged += OnExplodedChanged;
        SetExploded(m_Exploded.Value);   // catch up with the shared state
    }

    public override void OnNetworkDespawn()
    {
        m_Exploded.OnValueChanged -= OnExplodedChanged;
        base.OnNetworkDespawn();
    }

    void Update()
    {
        if (toggleInput.action != null && toggleInput.action.WasPressedThisFrame()) Toggle();
    }

    /// <summary>Explode / collapse. Also callable from a UI button.</summary>
    public void Toggle()
    {
        if (!IsSpawned)
        {
            SetExploded(!exploded);          // no network running: behave like the single-player original
            return;
        }

        if (IsOwner) m_Exploded.Value = !m_Exploded.Value;   // the owner publishes the new state
        else RequestToggleRpc();                             // everyone else asks the owner
    }

    [Rpc(SendTo.Owner)]
    void RequestToggleRpc() => m_Exploded.Value = !m_Exploded.Value;

    void OnExplodedChanged(bool previous, bool current) => SetExploded(current);

    void SetExploded(bool value)
    {
        exploded = value;
        if (exploded && factor == 0f) needCapture = true;    // measure once the parts exist
        else if (!exploded) needCapture = false;
        Debug.Log(exploded ? "Exploding" : "Collapsing");
    }

    void LateUpdate()
    {
        if (needCapture)
        {
            Capture();
            if (parts.Count > 0) needCapture = false;
        }

        float target = exploded ? 1f : 0f;
        if (parts.Count == 0 || (factor == 0f && target == 0f)) return; // assembled: leave parts alone

        factor = Mathf.MoveTowards(factor, target, speed * Time.deltaTime);
        float eased = Mathf.SmoothStep(0f, 1f, factor);
        foreach (var p in parts)
            if (p.t != null) p.t.localPosition = p.basePos + p.offset * eased;
    }

    /// <summary>Records each part's assembled position and its explode direction.</summary>
    void Capture()
    {
        parts.Clear();
        var renderers = GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return;

        // Centre of the whole assembly
        Vector3 centroid = Vector3.zero;
        foreach (var r in renderers) centroid += Centre(r);
        centroid /= renderers.Length;

        foreach (var r in renderers)
        {
            Transform t = r.transform;
            Vector3 dir = Centre(r) - centroid;                    // centre -> part
            if (dir.sqrMagnitude < 1e-8f) dir = -transform.up * 0.01f; // part sits on the centre
            parts.Add(new Part
            {
                t = t,
                basePos = t.localPosition,
                offset = t.parent.InverseTransformVector(dir * spread)
            });
        }
    }

    static Vector3 Centre(Renderer r) => r.enabled ? r.bounds.center : r.transform.position;
}
