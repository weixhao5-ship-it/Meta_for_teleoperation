LOCAL_PATH := $(call my-dir)

include $(CLEAR_VARS)
LOCAL_MODULE := ArmCameraGStreamer
LOCAL_SRC_FILES := ArmCameraGStreamer.cpp
LOCAL_CPPFLAGS += -std=c++17 -fno-exceptions -fno-rtti
LOCAL_SHARED_LIBRARIES := gstreamer_android
LOCAL_LDLIBS += -llog -landroid
include $(BUILD_SHARED_LIBRARY)

ifndef GSTREAMER_ROOT
ifndef GSTREAMER_ROOT_ANDROID
$(error Set GSTREAMER_ROOT_ANDROID to the extracted GStreamer Android SDK root)
endif
GSTREAMER_ROOT := $(GSTREAMER_ROOT_ANDROID)
endif

GSTREAMER_NDK_BUILD_PATH := $(GSTREAMER_ROOT)/share/gst-android/ndk-build
include $(GSTREAMER_NDK_BUILD_PATH)/plugins.mk
GSTREAMER_PLUGINS := coreelements app playback videoconvertscale udp rtp rtpmanager videoparsersbad androidmedia
GSTREAMER_EXTRA_DEPS := gstreamer-app-1.0 gstreamer-video-1.0
include $(GSTREAMER_NDK_BUILD_PATH)/gstreamer-1.0.mk
