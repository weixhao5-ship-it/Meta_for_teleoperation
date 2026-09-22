#include <android/log.h>
#include <gst/app/gstappsink.h>
#include <gst/gst.h>
#include <gst/video/video.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdint.h>
#include <string.h>

#define LOG_TAG "ArmCameraGStreamer"
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)

#if defined(__GNUC__)
#define EXPORT extern "C" __attribute__((visibility("default")))
#else
#define EXPORT extern "C"
#endif

namespace {

const gsize kMaximumFrameBytes = 128u * 1024u * 1024u;
const gsize kErrorCapacity = 512;

// decodebin exposes this signal through GObject introspection but does not install
// its private playback header. Keep the public enum values from decodebin's signal.
enum AutoplugSelectResult {
    kAutoplugTry = 0,
    kAutoplugExpose = 1,
    kAutoplugSkip = 2
};

pthread_mutex_t g_lifecycleMutex = PTHREAD_MUTEX_INITIALIZER;
pthread_mutex_t g_configMutex = PTHREAD_MUTEX_INITIALIZER;
pthread_mutex_t g_frameMutex = PTHREAD_MUTEX_INITIALIZER;
pthread_mutex_t g_errorMutex = PTHREAD_MUTEX_INITIALIZER;

int g_textureWidth = 0;
int g_textureHeight = 0;

GstElement* g_pipeline = nullptr;
GstElement* g_decodebin = nullptr;  // Own reference while the receiver is running.
GstElement* g_appsinkElement = nullptr;  // Own reference while the receiver is running.
GstBus* g_bus = nullptr;
pthread_t g_busThread;
gboolean g_busThreadStarted = FALSE;
volatile gint g_stopBusThread = 0;

int g_streamWidth = 0;
int g_streamHeight = 0;
gsize g_frameBufferBytes = 0;
guint8* g_latestFrame = nullptr;
guint64 g_latestFrameSequence = 0;
guint64 g_uploadedFrameSequence = 0;
gboolean g_receiverActive = FALSE;

gchar g_lastError[kErrorCapacity] = {};

void SetLastError(const gchar* format, ...) {
    gchar message[kErrorCapacity] = {};
    va_list args;
    va_start(args, format);
    g_vsnprintf(message, sizeof(message), format, args);
    va_end(args);

    pthread_mutex_lock(&g_errorMutex);
    g_strlcpy(g_lastError, message, sizeof(g_lastError));
    pthread_mutex_unlock(&g_errorMutex);

    if (message[0] != '\0') {
        LOGE("%s", message);
    }
}

gboolean FactoryIsAllowedHardwareH264Decoder(GstElementFactory* factory) {
    if (factory == nullptr) {
        return FALSE;
    }

    const gchar* name = gst_plugin_feature_get_name(GST_PLUGIN_FEATURE(factory));
    const gchar* pluginName = gst_plugin_feature_get_plugin_name(GST_PLUGIN_FEATURE(factory));
    const gchar* klass = gst_element_factory_get_metadata(factory, GST_ELEMENT_METADATA_KLASS);

    return name != nullptr && g_str_has_prefix(name, "amcviddec-") &&
           pluginName != nullptr && g_str_equal(pluginName, "androidmedia") &&
           klass != nullptr && g_str_has_prefix(klass, "Codec/Decoder/Video/") &&
           g_str_has_suffix(klass, "/Hardware");
}

gboolean FactoryHasH264SinkPad(GstElementFactory* factory, const GstCaps* h264Caps) {
    const GList* templates = gst_element_factory_get_static_pad_templates(factory);
    for (const GList* item = templates; item != nullptr; item = item->next) {
        const GstStaticPadTemplate* padTemplate =
            static_cast<const GstStaticPadTemplate*>(item->data);
        if (padTemplate == nullptr || padTemplate->direction != GST_PAD_SINK) {
            continue;
        }

        GstCaps* templateCaps = gst_static_caps_get(
            const_cast<GstStaticCaps*>(&padTemplate->static_caps));
        gboolean compatible = templateCaps != nullptr &&
            gst_caps_can_intersect(templateCaps, h264Caps);
        if (templateCaps != nullptr) {
            gst_caps_unref(templateCaps);
        }
        if (compatible) {
            return TRUE;
        }
    }
    return FALSE;
}

gboolean HasHardwareH264Decoder() {
    GstCaps* h264Caps = gst_caps_from_string(
        "video/x-h264,stream-format=(string)byte-stream,alignment=(string)au");
    if (h264Caps == nullptr) {
        SetLastError("Could not create byte-stream H.264 caps for decoder discovery");
        return FALSE;
    }

    GstElementFactoryListType decoderVideoType = static_cast<GstElementFactoryListType>(
        GST_ELEMENT_FACTORY_TYPE_DECODER | GST_ELEMENT_FACTORY_TYPE_MEDIA_VIDEO);
    GList* factories = gst_element_factory_list_get_elements(decoderVideoType, GST_RANK_NONE);
    gboolean found = FALSE;
    for (GList* item = factories; item != nullptr; item = item->next) {
        GstElementFactory* factory = GST_ELEMENT_FACTORY(item->data);
        if (FactoryIsAllowedHardwareH264Decoder(factory) &&
            FactoryHasH264SinkPad(factory, h264Caps)) {
            found = TRUE;
            break;
        }
    }

    gst_plugin_feature_list_free(factories);
    gst_caps_unref(h264Caps);

    if (!found) {
        SetLastError("No H.264 decoder is available from androidmedia: expected an amcviddec-* "
                     "factory with Codec/Decoder/Video/Hardware class and byte-stream H.264 sink caps. "
                     "Software decoder fallback is disabled.");
    }
    return found;
}

gint SelectDecoderFactory(GstElement*, GstPad*, GstCaps* caps,
                          GstElementFactory* factory, gpointer) {
    if (caps == nullptr || gst_caps_is_empty(caps) || gst_caps_is_any(caps)) {
        return kAutoplugSkip;
    }

    for (guint i = 0; i < gst_caps_get_size(caps); ++i) {
        const GstStructure* structure = gst_caps_get_structure(caps, i);
        const gchar* mediaType = structure != nullptr ? gst_structure_get_name(structure) : nullptr;
        if (mediaType != nullptr && g_str_equal(mediaType, "video/x-raw")) {
            return kAutoplugExpose;
        }
    }

    GstCaps* h264Caps = gst_caps_from_string("video/x-h264");
    gboolean isH264 = h264Caps != nullptr && gst_caps_can_intersect(caps, h264Caps);
    if (h264Caps != nullptr) {
        gst_caps_unref(h264Caps);
    }

    if (isH264 && FactoryIsAllowedHardwareH264Decoder(factory)) {
        const gchar* name = gst_plugin_feature_get_name(GST_PLUGIN_FEATURE(factory));
        LOGI("Allowing hardware H.264 decoder factory: %s", name != nullptr ? name : "(unknown)");
        return kAutoplugTry;
    }

    // Decodebin is never allowed to try a software decoder or a decoder from another plugin.
    return kAutoplugSkip;
}

GstFlowReturn OnNewSample(GstAppSink* sink, gpointer) {
    GstSample* sample = gst_app_sink_pull_sample(sink);
    if (sample == nullptr) {
        return GST_FLOW_EOS;
    }

    GstCaps* caps = gst_sample_get_caps(sample);
    GstBuffer* buffer = gst_sample_get_buffer(sample);
    GstVideoInfo videoInfo;
    gst_video_info_init(&videoInfo);
    if (caps == nullptr || buffer == nullptr || !gst_video_info_from_caps(&videoInfo, caps) ||
        GST_VIDEO_INFO_FORMAT(&videoInfo) != GST_VIDEO_FORMAT_RGBA ||
        GST_VIDEO_INFO_WIDTH(&videoInfo) != static_cast<guint>(g_streamWidth) ||
        GST_VIDEO_INFO_HEIGHT(&videoInfo) != static_cast<guint>(g_streamHeight)) {
        SetLastError("appsink received a frame with unexpected caps; expected RGBA %dx%d",
                     g_streamWidth, g_streamHeight);
        gst_sample_unref(sample);
        return GST_FLOW_ERROR;
    }

    GstVideoFrame videoFrame;
    if (!gst_video_frame_map(&videoFrame, &videoInfo, buffer, GST_MAP_READ)) {
        SetLastError("Could not map the decoded RGBA video frame");
        gst_sample_unref(sample);
        return GST_FLOW_ERROR;
    }

    const gint stride = GST_VIDEO_FRAME_PLANE_STRIDE(&videoFrame, 0);
    const gsize rowBytes = static_cast<gsize>(g_streamWidth) * 4u;
    const guint8* source = static_cast<const guint8*>(GST_VIDEO_FRAME_PLANE_DATA(&videoFrame, 0));
    if (source == nullptr || stride < 0 || static_cast<gsize>(stride) < rowBytes) {
        gst_video_frame_unmap(&videoFrame);
        gst_sample_unref(sample);
        SetLastError("Unsupported RGBA frame stride (%d bytes for a %" G_GSIZE_FORMAT " byte row)",
                     stride, rowBytes);
        return GST_FLOW_ERROR;
    }

    pthread_mutex_lock(&g_frameMutex);
    if (g_receiverActive && g_latestFrame != nullptr && g_frameBufferBytes >= rowBytes * g_streamHeight) {
        for (int row = 0; row < g_streamHeight; ++row) {
            // GStreamer rows are top-first; GL texture row zero is the bottom row.
            const int destinationRow = g_streamHeight - 1 - row;
            memcpy(g_latestFrame + static_cast<gsize>(destinationRow) * rowBytes,
                   source + static_cast<gsize>(row) * static_cast<gsize>(stride), rowBytes);
        }
        ++g_latestFrameSequence;
    }
    pthread_mutex_unlock(&g_frameMutex);

    gst_video_frame_unmap(&videoFrame);
    gst_sample_unref(sample);
    return GST_FLOW_OK;
}

void* BusMonitorThread(void* userdata) {
    GstBus* bus = GST_BUS(userdata);
    while (g_atomic_int_get(&g_stopBusThread) == 0) {
        GstMessage* message = gst_bus_timed_pop_filtered(
            bus, 200 * GST_MSECOND,
            static_cast<GstMessageType>(GST_MESSAGE_ERROR | GST_MESSAGE_EOS));
        if (message == nullptr) {
            continue;
        }

        if (GST_MESSAGE_TYPE(message) == GST_MESSAGE_ERROR) {
            GError* error = nullptr;
            gchar* debug = nullptr;
            gst_message_parse_error(message, &error, &debug);
            const gchar* sourceName = GST_MESSAGE_SRC(message) != nullptr
                ? GST_OBJECT_NAME(GST_MESSAGE_SRC(message)) : "GStreamer";
            if (debug != nullptr) {
                SetLastError("%s: %s (debug: %s)", sourceName,
                             error != nullptr && error->message != nullptr ? error->message : "pipeline error",
                             debug);
            } else {
                SetLastError("%s: %s", sourceName,
                             error != nullptr && error->message != nullptr ? error->message : "pipeline error");
            }
            if (error != nullptr) {
                g_error_free(error);
            }
            g_free(debug);
        } else if (GST_MESSAGE_TYPE(message) == GST_MESSAGE_EOS) {
            SetLastError("GStreamer receiver reached end of stream");
        }
        gst_message_unref(message);

        pthread_mutex_lock(&g_frameMutex);
        g_receiverActive = FALSE;
        pthread_mutex_unlock(&g_frameMutex);
        break;
    }
    return nullptr;
}

void ReleaseFrameBuffer() {
    pthread_mutex_lock(&g_frameMutex);
    g_receiverActive = FALSE;
    g_free(g_latestFrame);
    g_latestFrame = nullptr;
    g_frameBufferBytes = 0;
    g_latestFrameSequence = 0;
    g_uploadedFrameSequence = 0;
    g_streamWidth = 0;
    g_streamHeight = 0;
    pthread_mutex_unlock(&g_frameMutex);
}

void StopReceiverLocked() {
    GstElement* pipeline = g_pipeline;
    GstElement* decodebin = g_decodebin;
    GstElement* appsinkElement = g_appsinkElement;
    GstBus* bus = g_bus;

    pthread_mutex_lock(&g_frameMutex);
    g_receiverActive = FALSE;
    pthread_mutex_unlock(&g_frameMutex);

    g_atomic_int_set(&g_stopBusThread, 1);
    if (pipeline != nullptr) {
        gst_element_set_state(pipeline, GST_STATE_NULL);
    }
    if (g_busThreadStarted) {
        pthread_join(g_busThread, nullptr);
        g_busThreadStarted = FALSE;
    }

    g_pipeline = nullptr;
    g_decodebin = nullptr;
    g_appsinkElement = nullptr;
    g_bus = nullptr;

    if (bus != nullptr) {
        gst_object_unref(bus);
    }
    if (decodebin != nullptr) {
        gst_object_unref(decodebin);
    }
    if (appsinkElement != nullptr) {
        gst_object_unref(appsinkElement);
    }
    if (pipeline != nullptr) {
        gst_object_unref(pipeline);
    }

    ReleaseFrameBuffer();
}

gboolean AllocateFrameBuffer(int width, int height) {
    const gsize rowBytes = static_cast<gsize>(width) * 4u;
    if (height <= 0 || rowBytes == 0 ||
        static_cast<gsize>(height) > kMaximumFrameBytes / rowBytes) {
        SetLastError("Requested RGBA frame size %dx%d exceeds the 128 MiB limit", width, height);
        return FALSE;
    }

    const gsize bytes = rowBytes * static_cast<gsize>(height);
    guint8* frame = static_cast<guint8*>(g_malloc(bytes));
    if (frame == nullptr) {
        SetLastError("Could not allocate %" G_GSIZE_FORMAT " bytes for the latest video frame", bytes);
        return FALSE;
    }

    pthread_mutex_lock(&g_frameMutex);
    g_latestFrame = frame;
    g_frameBufferBytes = bytes;
    g_latestFrameSequence = 0;
    g_uploadedFrameSequence = 0;
    g_streamWidth = width;
    g_streamHeight = height;
    g_receiverActive = TRUE;
    pthread_mutex_unlock(&g_frameMutex);
    return TRUE;
}

gboolean StartReceiverLocked(int port, int width, int height) {
    StopReceiverLocked();
    SetLastError("");

    if (port < 1 || port > 65535 || width < 1 || height < 1) {
        SetLastError("Invalid receiver settings: port must be 1..65535 and dimensions must be positive");
        return FALSE;
    }
    if (!gst_is_initialized()) {
        SetLastError("GStreamer is not initialized. Call the official GStreamer.init(Context) helper "
                     "before StartGStreamerReceiver.");
        return FALSE;
    }

    int configuredWidth = 0;
    int configuredHeight = 0;
    pthread_mutex_lock(&g_configMutex);
    configuredWidth = g_textureWidth;
    configuredHeight = g_textureHeight;
    pthread_mutex_unlock(&g_configMutex);
    if ((configuredWidth > 0 && configuredWidth != width) ||
        (configuredHeight > 0 && configuredHeight != height)) {
        SetLastError("Receiver size %dx%d does not match ConfigureVideoTexture size %dx%d",
                     width, height, configuredWidth, configuredHeight);
        return FALSE;
    }
    if (configuredWidth == 0 || configuredHeight == 0) {
        pthread_mutex_lock(&g_configMutex);
        g_textureWidth = width;
        g_textureHeight = height;
        pthread_mutex_unlock(&g_configMutex);
    }

    if (!HasHardwareH264Decoder()) {
        return FALSE;
    }
    if (!AllocateFrameBuffer(width, height)) {
        return FALSE;
    }

    gchar* description = g_strdup_printf(
        "udpsrc name=video_source port=%d "
        "caps=\"application/x-rtp,media=(string)video,clock-rate=(int)90000,"
        "encoding-name=(string)H264,payload=(int)96\" "
        "! rtpjitterbuffer latency=60 "
        "! rtph264depay "
        "! h264parse disable-passthrough=true "
        "! video/x-h264,stream-format=(string)byte-stream,alignment=(string)au "
        "! decodebin name=video_decoder "
        "! videoconvert "
        "! videoscale "
        "! video/x-raw,format=RGBA,width=(int)%d,height=(int)%d,pixel-aspect-ratio=(fraction)1/1 "
        "! appsink name=video_sink sync=false max-buffers=1 drop=true",
        port, width, height);

    GError* parseError = nullptr;
    GstElement* pipeline = gst_parse_launch(description, &parseError);
    g_free(description);
    if (pipeline == nullptr || parseError != nullptr || !GST_IS_PIPELINE(pipeline)) {
        SetLastError("Could not create GStreamer pipeline: %s",
                     parseError != nullptr && parseError->message != nullptr
                         ? parseError->message : "gst_parse_launch did not return a pipeline");
        if (parseError != nullptr) {
            g_error_free(parseError);
        }
        if (pipeline != nullptr) {
            gst_object_unref(pipeline);
        }
        ReleaseFrameBuffer();
        return FALSE;
    }
    if (parseError != nullptr) {
        g_error_free(parseError);
    }

    GstElement* decodebin = gst_bin_get_by_name(GST_BIN(pipeline), "video_decoder");
    GstElement* appsinkElement = gst_bin_get_by_name(GST_BIN(pipeline), "video_sink");
    if (decodebin == nullptr || appsinkElement == nullptr || !GST_IS_APP_SINK(appsinkElement)) {
        SetLastError("Pipeline is missing decodebin or appsink");
        if (decodebin != nullptr) gst_object_unref(decodebin);
        if (appsinkElement != nullptr) gst_object_unref(appsinkElement);
        gst_object_unref(pipeline);
        ReleaseFrameBuffer();
        return FALSE;
    }

    g_signal_connect(decodebin, "autoplug-select", G_CALLBACK(SelectDecoderFactory), nullptr);
    GstAppSinkCallbacks callbacks = {};
    callbacks.new_sample = OnNewSample;
    gst_app_sink_set_callbacks(GST_APP_SINK(appsinkElement), &callbacks, nullptr, nullptr);
    g_object_set(G_OBJECT(appsinkElement), "max-buffers", static_cast<guint>(1),
                 "drop", TRUE, "sync", FALSE, nullptr);

    GstBus* bus = gst_element_get_bus(pipeline);
    if (bus == nullptr) {
        SetLastError("Could not obtain the GStreamer pipeline bus");
        gst_object_unref(decodebin);
        gst_object_unref(appsinkElement);
        gst_object_unref(pipeline);
        ReleaseFrameBuffer();
        return FALSE;
    }

    g_pipeline = pipeline;
    g_decodebin = decodebin;
    g_appsinkElement = appsinkElement;
    g_bus = bus;
    g_atomic_int_set(&g_stopBusThread, 0);

    GstStateChangeReturn stateResult = gst_element_set_state(g_pipeline, GST_STATE_PLAYING);
    if (stateResult == GST_STATE_CHANGE_FAILURE) {
        SetLastError("GStreamer pipeline could not enter PLAYING state");
        StopReceiverLocked();
        return FALSE;
    }

    int threadResult = pthread_create(&g_busThread, nullptr, BusMonitorThread, g_bus);
    if (threadResult != 0) {
        SetLastError("Could not start GStreamer bus monitor thread (pthread error %d)", threadResult);
        StopReceiverLocked();
        return FALSE;
    }
    g_busThreadStarted = TRUE;

    LOGI("Started RTP/H.264 receiver on UDP port %d at %dx%d", port, width, height);
    return TRUE;
}

int CopyLatestFrameLocked(void* destination, int capacity) {
    if (destination == nullptr || capacity <= 0) {
        SetLastError("CopyLatestFrame requires a valid destination buffer and positive capacity");
        return -1;
    }

    pthread_mutex_lock(&g_frameMutex);
    if (!g_receiverActive || g_latestFrame == nullptr || g_latestFrameSequence == 0 ||
        g_latestFrameSequence == g_uploadedFrameSequence) {
        pthread_mutex_unlock(&g_frameMutex);
        return 0;
    }
    if (g_frameBufferBytes > static_cast<gsize>(capacity)) {
        SetLastError("CopyLatestFrame destination is too small: need %" G_GSIZE_FORMAT
                     " bytes, received %d", g_frameBufferBytes, capacity);
        pthread_mutex_unlock(&g_frameMutex);
        return -1;
    }

    const int copiedBytes = static_cast<int>(g_frameBufferBytes);
    memcpy(destination, g_latestFrame, g_frameBufferBytes);
    g_uploadedFrameSequence = g_latestFrameSequence;
    pthread_mutex_unlock(&g_frameMutex);
    return copiedBytes;
}

}  // namespace

EXPORT void ConfigureVideoTexture(int width, int height) {
    pthread_mutex_lock(&g_lifecycleMutex);
    if (width <= 0 || height <= 0) {
        SetLastError("ConfigureVideoTexture requires positive dimensions");
        pthread_mutex_unlock(&g_lifecycleMutex);
        return;
    }

    pthread_mutex_lock(&g_configMutex);
    if (g_pipeline != nullptr && (width != g_textureWidth || height != g_textureHeight)) {
        pthread_mutex_unlock(&g_configMutex);
        SetLastError("Stop the GStreamer receiver before changing its texture dimensions");
        pthread_mutex_unlock(&g_lifecycleMutex);
        return;
    }
    g_textureWidth = width;
    g_textureHeight = height;
    pthread_mutex_unlock(&g_configMutex);
    pthread_mutex_unlock(&g_lifecycleMutex);
}

EXPORT int CopyLatestFrame(void* destination, int capacity) {
    return CopyLatestFrameLocked(destination, capacity);
}

EXPORT int StartGStreamerReceiver(int port, int width, int height) {
    pthread_mutex_lock(&g_lifecycleMutex);
    gboolean started = StartReceiverLocked(port, width, height);
    pthread_mutex_unlock(&g_lifecycleMutex);
    return started ? 1 : 0;
}

EXPORT void StopGStreamerReceiver() {
    pthread_mutex_lock(&g_lifecycleMutex);
    StopReceiverLocked();
    pthread_mutex_unlock(&g_lifecycleMutex);
}

EXPORT const char* GetLastGStreamerError() {
    static __thread gchar snapshot[kErrorCapacity];
    pthread_mutex_lock(&g_errorMutex);
    g_strlcpy(snapshot, g_lastError, sizeof(snapshot));
    pthread_mutex_unlock(&g_errorMutex);
    return snapshot;
}
