using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One reachability marker: a flat translucent disc laid on an AprilTag plus a billboarded
/// label above it. Built in code by <see cref="Create"/> (no prefab to maintain); the disc
/// material must be a serialized asset so its transparent URP variant survives Android shader
/// stripping. Colour goes through a MaterialPropertyBlock so no material instances are created.
/// </summary>
public class ReachabilityIndicatorView : MonoBehaviour
{
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    [SerializeField] private Renderer disc;
    [SerializeField] private TMP_Text label;
    [SerializeField] private Billboard billboard;

    private MaterialPropertyBlock _propertyBlock;
    private Color _color = Color.grey;
    private float _visibility = 1f;

    public static ReachabilityIndicatorView Create(string name, Transform parent, Material discMaterial,
                                                   TMP_FontAsset labelFont, Transform billboardCamera, float labelScale)
    {
        var root = new GameObject(name);
        root.transform.SetParent(parent, false);
        var view = root.AddComponent<ReachabilityIndicatorView>();

        var discObject = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        discObject.name = "Disc";
        var collider = discObject.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);
        discObject.transform.SetParent(root.transform, false);
        view.disc = discObject.GetComponent<Renderer>();
        if (discMaterial != null)
            view.disc.sharedMaterial = discMaterial;
        view.disc.shadowCastingMode = ShadowCastingMode.Off;
        view.disc.receiveShadows = false;

        var labelObject = new GameObject("Label");
        labelObject.transform.SetParent(root.transform, false);
        labelObject.transform.localScale = Vector3.one * labelScale;
        var text = labelObject.AddComponent<TextMeshPro>();
        if (labelFont != null)
            text.font = labelFont;
        text.fontSize = 1f;                         // 1 unit tall before labelScale (TMP 3D: size 10 = 1 m)
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.rectTransform.sizeDelta = new Vector2(10f, 1f);
        text.color = Color.white;
        view.label = text;
        view.billboard = labelObject.AddComponent<Billboard>();
        view.billboard.cameraTransform = billboardCamera;

        return view;
    }

    /// <summary>Sizes the disc (diameter in metres) and lifts the label above it along the disc normal.</summary>
    public void Init(float discDiameter, float labelHeight)
    {
        // The cylinder primitive is 1 unit wide and 2 units tall, centred on its origin.
        disc.transform.localScale = new Vector3(discDiameter, 0.0005f, discDiameter);
        label.transform.localPosition = new Vector3(0f, labelHeight, 0f);
    }

    public void SetPose(Vector3 position, Quaternion rotation) => transform.SetPositionAndRotation(position, rotation);

    public void SetState(Color color, string text)
    {
        _color = color;
        if (label.text != text)
            label.text = text;
        Apply();
    }

    public void SetVisibility(float visibility)
    {
        _visibility = Mathf.Clamp01(visibility);
        bool visible = _visibility > 0f;
        if (gameObject.activeSelf != visible)
            gameObject.SetActive(visible);
        if (visible)
            Apply();
    }

    private void Apply()
    {
        if (disc != null)
        {
            var c = _color;
            c.a *= _visibility;
            _propertyBlock ??= new MaterialPropertyBlock();
            disc.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetColor(BaseColorId, c);
            _propertyBlock.SetColor(ColorId, c);
            disc.SetPropertyBlock(_propertyBlock);
        }
        if (label != null)
        {
            var lc = label.color;
            lc.a = _visibility;
            label.color = lc;
        }
    }
}
