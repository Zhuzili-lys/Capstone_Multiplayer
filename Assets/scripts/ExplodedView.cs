using System.Collections.Generic;
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
/// Usage: add to the same GameObject as ParametricDrone. Works on any model whose
/// parts are separate child objects (e.g. an imported CAD assembly) too.
/// </summary>
public class ExplodedView : MonoBehaviour
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
    ParametricDrone drone;

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

    void Update()
    {
        if (toggleInput.action.WasPressedThisFrame()) Toggle();
    }

    /// <summary>Also callable from a UI button.</summary>
    public void Toggle()
    {
        exploded = !exploded;
        if (exploded && factor == 0f) Capture();
        Debug.Log(exploded ? "Exploding" : "Collapsing");
    }

    void LateUpdate()
    {
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
