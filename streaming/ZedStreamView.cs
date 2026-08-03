using UnityEngine;

public class ZedStreamView : MonoBehaviour
{
    [Header("Network")]
    public int port = 5600;

    [Header("Render")]
    public Renderer targetRenderer;      // SBS Quad의 Renderer
    public int texWidth = 2560;
    public int texHeight = 720;

    private AndroidJavaObject receiver;
    private Texture2D extTex;
    private AndroidJavaObject surfaceTexture;
    private AndroidJavaObject javaSurface;
    private int texId;

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        StartCoroutine(InitAfterGL());
#else
        Debug.LogWarning("ZedStreamView: Android 전용");
#endif
    }

    System.Collections.IEnumerator InitAfterGL()
    {
        yield return new WaitForEndOfFrame();

        // 1) 외부 OES 텍스처 생성
        texId = CreateExternalTexture();

        // 2) SurfaceTexture + Surface 생성
        surfaceTexture = new AndroidJavaObject("android.graphics.SurfaceTexture", texId);
        surfaceTexture.Call("setDefaultBufferSize", texWidth, texHeight);
        javaSurface = new AndroidJavaObject("android.view.Surface", surfaceTexture);

        // 3) Unity 텍스처로 래핑
        extTex = Texture2D.CreateExternalTexture(
            texWidth, texHeight, TextureFormat.RGBA32, false, false,
            (System.IntPtr)texId);
        targetRenderer.material.mainTexture = extTex;

        // 4) 수신 시작
        receiver = new AndroidJavaObject("com.rainbow.zedstream.RtpH264Receiver",
                                          javaSurface, port);
        receiver.Call("start");
        Debug.Log($"ZedStreamView: listening on UDP {port}");
    }

    void Update()
    {
        if (surfaceTexture != null)
            surfaceTexture.Call("updateTexImage");
    }

    void OnDestroy()
    {
        if (receiver != null) { receiver.Call("stop"); receiver.Dispose(); }
        if (javaSurface != null) { javaSurface.Call("release"); javaSurface.Dispose(); }
        if (surfaceTexture != null) { surfaceTexture.Call("release"); surfaceTexture.Dispose(); }
    }

    private int CreateExternalTexture()
    {
        // GLES 외부 텍스처 ID 발급
        var tex = new Texture2D(1, 1);
        return (int)(long)tex.GetNativeTexturePtr();
    }
}