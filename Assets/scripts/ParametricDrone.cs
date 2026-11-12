using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

/// <summary>
/// Builds a simple quadcopter from Unity primitives, driven by design parameters
/// (values from the Fusion 360 "Drone Design 101" example), and makes it grabbable in VR.
///
/// Multiplayer: the design parameters are shared through a NetworkVariable so every player sees the
/// same drone, and the root pose/scale is replicated by the ClientNetworkTransform on the prefab.
/// Whoever grabs the drone takes ownership, so the pose they produce is the one that is replicated.
/// With no NetworkManager running the component behaves exactly like the single-player original.
///
/// Usage: create an empty GameObject in the VR Template scene, add this component, press Play.
/// Change the parameters in the Inspector while playing; the drone updates live.
/// Requires XR Interaction Toolkit 3.x (included in Unity's VR Template).
/// </summary>
public class ParametricDrone : NetworkBehaviour
{
    [Header("Design parameters (millimetres)")]
    [Range(120f, 450f)] public float frameSize = 210f;      // motor-to-motor diagonal
    [Range(6f, 30f)]    public float armWidth = 12f;
    [Range(80f, 250f)]  public float plateLength = 160f;
    [Range(30f, 90f)]   public float plateWidth = 45f;
    [Range(10f, 50f)]   public float plateGap = 25f;          // standoff height
    [Range(1f, 6f)]     public float bottomPlateThickness = 3f;
    [Range(1f, 6f)]     public float topPlateThickness = 1.5f;
    [Range(15f, 40f)]   public float motorDiameter = 25f;
    [Range(50f, 200f)]  public float propDiameter = 127f;     // 5 inch props

    [Header("Display")]
    [Tooltip("Scales the whole drone so it is comfortable to inspect in VR (1 = real size).")]
    [Range(1f, 10f)] public float displayScale = 3f;

    Transform root;
    Transform bottomPlate, topPlate;
    readonly Transform[] arms = new Transform[4];
    readonly Transform[] motors = new Transform[4];
    readonly Transform[] props = new Transform[4];
    bool dirty = true;

    /// <summary>Raised after the drone is rebuilt from new parameters (used by ExplodedView).</summary>
    public event System.Action Rebuilt;

    const float MM = 0.001f; // millimetres to metres

    /// <summary>Shared design parameters. Written by the drone's current owner, read by everyone.</summary>
    readonly NetworkVariable<DroneParameters> m_Parameters = new NetworkVariable<DroneParameters>(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    void Start()
    {
        BuildParts();
        Apply();
        MakeGrabbable();
    }

    void OnValidate() => dirty = true;   // Inspector change

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        m_Parameters.OnValueChanged += OnParametersChanged;

        if (IsOwner)
            m_Parameters.Value = CaptureParameters();   // publish the Inspector values to everyone
        else
            LoadParameters(m_Parameters.Value);

        dirty = true;                                   // rebuild with the shared values
    }

    public override void OnNetworkDespawn()
    {
        m_Parameters.OnValueChanged -= OnParametersChanged;
        base.OnNetworkDespawn();
    }

    void Update()
    {
        if (dirty && root != null)
        {
            Apply();
            PublishParameters();
        }
    }

    /// <summary>Call this from UI sliders, e.g. SetParameter(v => drone.frameSize = v).</summary>
    public void MarkDirty() => dirty = true;

    void PublishParameters()
    {
        if (!IsSpawned || !IsOwner) return;

        DroneParameters current = CaptureParameters();
        if (!current.Equals(m_Parameters.Value))
            m_Parameters.Value = current;
    }

    void OnParametersChanged(DroneParameters previous, DroneParameters current)
    {
        LoadParameters(current);
        dirty = true;
    }

    void BuildParts()
    {
        root = new GameObject("DroneGeometry").transform;
        root.SetParent(transform, false);

        Material frameMat = MakeMaterial(new Color(0.15f, 0.15f, 0.17f));
        Material topMat   = MakeMaterial(new Color(0.25f, 0.45f, 0.75f));
        Material motorMat = MakeMaterial(new Color(0.75f, 0.75f, 0.78f));
        Material propMat  = MakeMaterial(new Color(0.95f, 0.55f, 0.15f, 1f));

        bottomPlate = Part(PrimitiveType.Cube, "BottomPlate", frameMat);
        topPlate    = Part(PrimitiveType.Cube, "TopPlate", topMat);
        for (int i = 0; i < 4; i++)
        {
            arms[i]   = Part(PrimitiveType.Cube, $"Arm_{i}", frameMat);
            motors[i] = Part(PrimitiveType.Cylinder, $"Motor_{i}", motorMat);
            props[i]  = Part(PrimitiveType.Cylinder, $"Prop_{i}", propMat);
        }
    }

    /// <summary>Repositions and resizes every part from the current parameters.</summary>
    void Apply()
    {
        dirty = false;
        root.localScale = Vector3.one * displayScale;

        float motorRadius = frameSize * 0.5f * MM;           // centre to motor
        float bottomT = bottomPlateThickness * MM;
        float topT = topPlateThickness * MM;

        bottomPlate.localPosition = Vector3.zero;
        bottomPlate.localScale = new Vector3(plateWidth * MM, bottomT, plateLength * MM);

        topPlate.localPosition = new Vector3(0, plateGap * MM, 0);
        topPlate.localScale = new Vector3(plateWidth * MM, topT, plateLength * MM);

        float motorH = 0.02f; // 20 mm tall motor
        for (int i = 0; i < 4; i++)
        {
            float angle = 45f + 90f * i;                      // motors on the 45° diagonals
            Vector3 dir = Quaternion.Euler(0, angle, 0) * Vector3.forward;
            Vector3 motorPos = dir * motorRadius;

            // Arm runs from the centre to the motor
            arms[i].localPosition = motorPos * 0.5f;
            arms[i].localRotation = Quaternion.LookRotation(dir);
            arms[i].localScale = new Vector3(armWidth * MM, bottomT, motorRadius);

            // Unity cylinder is 2 units tall, 1 unit wide
            motors[i].localPosition = motorPos + Vector3.up * (motorH * 0.5f + bottomT * 0.5f);
            motors[i].localScale = new Vector3(motorDiameter * MM, motorH * 0.5f, motorDiameter * MM);

            props[i].localPosition = motorPos + Vector3.up * (motorH + bottomT + 0.003f);
            props[i].localScale = new Vector3(propDiameter * MM, 0.001f, propDiameter * MM);
        }

        Rebuilt?.Invoke();
    }

    void MakeGrabbable()
    {
        if (!TryGetComponent(out Rigidbody rb))
            rb = gameObject.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        // Added before the interactable so it is used instead of the default transformer
        if (!TryGetComponent(out XRGeneralGrabTransformer transformer))
            transformer = gameObject.AddComponent<XRGeneralGrabTransformer>();
        transformer.allowTwoHandedScaling = true;   // grab with both hands and pull apart to scale

        if (!TryGetComponent(out XRGrabInteractable grab))
            grab = gameObject.AddComponent<XRGrabInteractable>();
        grab.selectMode = InteractableSelectMode.Multiple; // allow two hands
        grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
        grab.throwOnDetach = false;                 // the model stays where you let go
        grab.useDynamicAttach = true;               // grab from where your hand is, no snapping

        grab.selectEntered.AddListener(OnGrabbed);
    }

    // Whoever grabs the drone becomes its owner, so the pose/scale they produce is replicated.
    void OnGrabbed(SelectEnterEventArgs args)
    {
        if (!IsSpawned || IsOwner) return;
        NetworkObject.ChangeOwnership(NetworkManager.LocalClientId);
    }

    Transform Part(PrimitiveType type, string name, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(root, false);
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go.transform;
    }

    static Material MakeMaterial(Color c)
    {
        // VR Template uses URP; fall back to Built-in if needed
        Shader s = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(s);
        m.color = c;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        return m;
    }

    DroneParameters CaptureParameters() => new DroneParameters
    {
        frameSize = frameSize,
        armWidth = armWidth,
        plateLength = plateLength,
        plateWidth = plateWidth,
        plateGap = plateGap,
        bottomPlateThickness = bottomPlateThickness,
        topPlateThickness = topPlateThickness,
        motorDiameter = motorDiameter,
        propDiameter = propDiameter,
        displayScale = displayScale,
    };

    void LoadParameters(DroneParameters p)
    {
        frameSize = p.frameSize;
        armWidth = p.armWidth;
        plateLength = p.plateLength;
        plateWidth = p.plateWidth;
        plateGap = p.plateGap;
        bottomPlateThickness = p.bottomPlateThickness;
        topPlateThickness = p.topPlateThickness;
        motorDiameter = p.motorDiameter;
        propDiameter = p.propDiameter;
        displayScale = p.displayScale;
    }

    /// <summary>The full set of design parameters, serialized for the network.</summary>
    public struct DroneParameters : INetworkSerializable, System.IEquatable<DroneParameters>
    {
        public float frameSize, armWidth, plateLength, plateWidth, plateGap;
        public float bottomPlateThickness, topPlateThickness, motorDiameter, propDiameter, displayScale;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref frameSize);
            serializer.SerializeValue(ref armWidth);
            serializer.SerializeValue(ref plateLength);
            serializer.SerializeValue(ref plateWidth);
            serializer.SerializeValue(ref plateGap);
            serializer.SerializeValue(ref bottomPlateThickness);
            serializer.SerializeValue(ref topPlateThickness);
            serializer.SerializeValue(ref motorDiameter);
            serializer.SerializeValue(ref propDiameter);
            serializer.SerializeValue(ref displayScale);
        }

        public bool Equals(DroneParameters other) =>
            frameSize == other.frameSize && armWidth == other.armWidth && plateLength == other.plateLength &&
            plateWidth == other.plateWidth && plateGap == other.plateGap &&
            bottomPlateThickness == other.bottomPlateThickness && topPlateThickness == other.topPlateThickness &&
            motorDiameter == other.motorDiameter && propDiameter == other.propDiameter &&
            displayScale == other.displayScale;

        public override bool Equals(object obj) => obj is DroneParameters other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + frameSize.GetHashCode();
                hash = hash * 31 + armWidth.GetHashCode();
                hash = hash * 31 + plateLength.GetHashCode();
                hash = hash * 31 + plateWidth.GetHashCode();
                hash = hash * 31 + plateGap.GetHashCode();
                hash = hash * 31 + bottomPlateThickness.GetHashCode();
                hash = hash * 31 + topPlateThickness.GetHashCode();
                hash = hash * 31 + motorDiameter.GetHashCode();
                hash = hash * 31 + propDiameter.GetHashCode();
                hash = hash * 31 + displayScale.GetHashCode();
                return hash;
            }
        }
    }
}
