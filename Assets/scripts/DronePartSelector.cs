using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Select and hide drone parts with EITHER controller (board functions: "Select part" and "Show / hide").
///
///   Point a controller at a part           -> part highlights yellow
///   Trigger                                -> select it (turns cyan)
///   A / X button (primary)                 -> hide the selected part
///   A / X button while pointing at nothing -> show all parts again
///
/// Laptop simulator keys: T = trigger, 1 = primary (A).
/// For the left controller hold Shift (Shift+T, Shift+1).
///
/// Multiplayer: which parts are hidden is shared through a NetworkList (by part name), so hiding a
/// part hides it for everyone. Any player may hide/restore; the request is forwarded to the drone's
/// owner, which owns the list. Hover highlight, the selection ray and the current selection stay
/// local, because they are private to the hand that is pointing.
///
/// Usage: add to the same GameObject as ParametricDrone. The controllers are found
/// automatically by name ("Right Controller" / "Left Controller") if the fields are
/// left empty; drag them in manually only if your rig uses different names.
/// </summary>
public class DronePartSelector : NetworkBehaviour
{
    [Tooltip("The right controller transform (XR Origin > Camera Offset > Right Controller).")]
    public Transform rayOrigin;
    [Tooltip("The left controller transform (XR Origin > Camera Offset > Left Controller). Optional.")]
    public Transform leftRayOrigin;
    public float maxDistance = 10f;
    [Tooltip("Ray thickness in metres. A little tolerance makes small parts easier to hit.")]
    public float rayRadius = 0.015f;
    [Tooltip("Draw a thin line showing exactly where the selection ray points.")]
    public bool showSelectionRay = true;

    [Header("Highlight colours")]
    public Color hoverColor = new Color(1f, 0.85f, 0.2f);
    public Color selectedColor = new Color(0.2f, 0.85f, 1f);

    class Hand
    {
        public string name;
        public InputAction select, hide;
        public Transform hovered;
        public Transform aim;          // where the ray starts and points
        public LineRenderer line;      // visible selection ray
    }

    Hand right, left;
    Transform selected;
    readonly List<Transform> hiddenParts = new List<Transform>();
    MaterialPropertyBlock block;

    /// <summary>Names of the parts hidden from every player. Written by the drone's owner, read by everyone.</summary>
    readonly NetworkList<FixedString64Bytes> m_HiddenParts = new NetworkList<FixedString64Bytes>(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    bool hiddenDirty;   // the shared list changed and the visuals need re-applying

    void OnEnable()
    {
        block = new MaterialPropertyBlock();
        right = MakeHand("Right", "RightHand");
        left  = MakeHand("Left",  "LeftHand");
    }

    void Start()
    {
        // Auto-connect: if the fields are empty, find the controllers by name.
        // This repairs the links automatically when the drone is moved into another scene.
        if (rayOrigin == null) rayOrigin = FindController("Right Controller");
        if (leftRayOrigin == null) leftRayOrigin = FindController("Left Controller");

        // Aim from the same point as the XR toolkit's visible ray (its Near-Far / Ray interactor),
        // so the selection matches the white line you see.
        right.aim = FindAim(rayOrigin);
        left.aim  = FindAim(leftRayOrigin);
        right.line = MakeLine("Right Selection Ray");
        left.line  = MakeLine("Left Selection Ray");

        Debug.Log($"DronePartSelector: right ray = {(right.aim ? right.aim.name : "NOT FOUND")}, " +
                  $"left ray = {(left.aim ? left.aim.name : "NOT FOUND")}");
    }

    static Transform FindAim(Transform controller)
    {
        if (controller == null) return null;
        foreach (var t in controller.GetComponentsInChildren<Transform>(true))
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("near-far") || n.Contains("nearfar") || n.Contains("ray interactor"))
                return t;
        }
        return controller;
    }

    LineRenderer MakeLine(string name)
    {
        var go = new GameObject(name);
        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.startWidth = 0.003f;
        lr.endWidth = 0.003f;
        lr.useWorldSpace = true;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.enabled = showSelectionRay;
        return lr;
    }

    static Transform FindController(string exactName)
    {
        var go = GameObject.Find(exactName);
        return go != null ? go.transform : null;
    }

    void OnDisable()
    {
        foreach (var h in new[] { right, left })
        {
            h?.select.Disable();
            h?.hide.Disable();
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_HiddenParts.OnListChanged += OnHiddenPartsChanged;
        hiddenDirty = true;   // the shared list may not be empty; apply it on the next Update
    }

    public override void OnNetworkDespawn()
    {
        m_HiddenParts.OnListChanged -= OnHiddenPartsChanged;
        base.OnNetworkDespawn();
    }

    static Hand MakeHand(string name, string usage)
    {
        var h = new Hand
        {
            name = name,
            select = new InputAction(name + " Select", InputActionType.Button, $"<XRController>{{{usage}}}/{{TriggerButton}}"),
            hide   = new InputAction(name + " Hide",   InputActionType.Button, $"<XRController>{{{usage}}}/{{PrimaryButton}}")
        };
        h.select.Enable();
        h.hide.Enable();
        return h;
    }

    void Update()
    {
        if (hiddenDirty)
        {
            hiddenDirty = false;
            ApplyHiddenState();
        }

        if (right.aim != null) Process(right, right.aim);
        if (left.aim != null) Process(left, left.aim);
    }

    void Process(Hand hand, Transform origin)
    {
        // 1. Hover: nearest drone part along the ray (ignores controllers, hands, floor, etc.)
        Transform hit = null;
        float hitDist = maxDistance;
        var hits = Physics.SphereCastAll(origin.position, rayRadius, origin.forward, maxDistance);
        foreach (var h in hits)
        {
            if (h.collider.transform.IsChildOf(transform) && h.distance < hitDist)
            {
                hit = h.collider.transform;
                hitDist = h.distance;
            }
        }

        if (hand.line != null)
        {
            hand.line.enabled = showSelectionRay;
            float len = hit != null ? hitDist : Mathf.Min(maxDistance, 3f);
            hand.line.SetPosition(0, origin.position);
            hand.line.SetPosition(1, origin.position + origin.forward * len);
            Color c = hit != null ? hoverColor : new Color(1f, 1f, 1f, 0.35f);
            hand.line.startColor = c;
            hand.line.endColor = c;
        }

        if (hit != hand.hovered)
        {
            Transform old = hand.hovered;
            hand.hovered = hit;
            RefreshHighlight(old);
            RefreshHighlight(hit);
        }

        // Diagnostics: report every button press, even when nothing is hovered
        if (hand.select.WasPressedThisFrame())
            Debug.Log($"[{hand.name}] trigger pressed, pointing at: {(hand.hovered ? hand.hovered.name : "nothing")}");
        if (hand.hide.WasPressedThisFrame())
            Debug.Log($"[{hand.name}] primary (A/X) pressed, pointing at: {(hand.hovered ? hand.hovered.name : "nothing")}");

        // 2. Trigger: select the hovered part
        if (hand.select.WasPressedThisFrame() && hand.hovered != null)
        {
            Transform old = selected;
            selected = hand.hovered;
            RefreshHighlight(old);
            RefreshHighlight(selected);
            Debug.Log($"{hand.name} hand selected {selected.name}");
        }

        // 3. Primary (A / X): hide selected part, or show all if pointing at nothing
        if (hand.hide.WasPressedThisFrame())
        {
            if (hand.hovered == null) ShowAll();
            else if (selected != null) Hide(selected);
            else Debug.Log("Nothing selected yet: point at a part and press the trigger first");
        }
    }

    void Hide(Transform part)
    {
        if (part == null) return;

        if (selected == part) selected = null;
        if (right.hovered == part) right.hovered = null;
        if (left.hovered == part) left.hovered = null;

        if (!IsSpawned)
        {
            // No network running: hide it here, like the single-player original.
            SetVisible(part, false);
            hiddenParts.Add(part);
            Debug.Log($"Hid {part.name}");
            return;
        }

        // The hidden list belongs to the drone's owner; forward the request if we are not it.
        if (IsOwner) AddHidden(part.name);
        else RequestHideRpc(part.name);
    }

    void ShowAll()
    {
        selected = null;

        if (!IsSpawned)
        {
            SetAllVisible();
            return;
        }

        if (IsOwner) m_HiddenParts.Clear();
        else RequestShowAllRpc();
    }

    [Rpc(SendTo.Owner)]
    void RequestHideRpc(string partName) => AddHidden(partName);

    [Rpc(SendTo.Owner)]
    void RequestShowAllRpc() => m_HiddenParts.Clear();

    void AddHidden(string partName)
    {
        FixedString64Bytes name = partName;
        if (!m_HiddenParts.Contains(name)) m_HiddenParts.Add(name);
    }

    void OnHiddenPartsChanged(NetworkListEvent<FixedString64Bytes> changeEvent) => hiddenDirty = true;

    void SetAllVisible()
    {
        hiddenParts.Clear();
        foreach (var r in GetComponentsInChildren<Renderer>(true)) SetVisible(r.transform, true);
        Debug.Log("Showing all parts");
    }

    /// <summary>Hides exactly the parts in the shared list and shows every other part.</summary>
    void ApplyHiddenState()
    {
        hiddenParts.Clear();
        foreach (var r in GetComponentsInChildren<Renderer>(true))
        {
            Transform t = r.transform;
            bool hidden = IsHidden(t.name);
            SetVisible(t, !hidden);
            if (hidden) hiddenParts.Add(t);
        }
        Debug.Log($"Hidden parts synced ({hiddenParts.Count} hidden)");
    }

    bool IsHidden(string partName)
    {
        foreach (var h in m_HiddenParts)
            if (h.ToString() == partName) return true;
        return false;
    }

    static void SetVisible(Transform part, bool visible)
    {
        var r = part.GetComponent<Renderer>();
        if (r != null)
        {
            if (!visible) r.SetPropertyBlock(null);   // drop the highlight before hiding
            r.enabled = visible;
        }

        var c = part.GetComponent<Collider>();
        if (c != null) c.enabled = visible;
    }

    // Selected = cyan, hovered by either hand = yellow, otherwise normal colour
    void RefreshHighlight(Transform part)
    {
        if (part == null) return;
        var r = part.GetComponent<Renderer>();
        if (r == null) return;

        Color? c = null;
        if (part == selected) c = selectedColor;
        else if (part == right.hovered || part == left.hovered) c = hoverColor;

        if (c == null) { r.SetPropertyBlock(null); return; }
        block.Clear();
        block.SetColor("_BaseColor", c.Value); // URP
        block.SetColor("_Color", c.Value);     // Built-in
        r.SetPropertyBlock(block);
    }
}
