package com.rainbow.zedstream;

import android.media.MediaCodec;
import android.media.MediaFormat;
import android.view.Surface;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.nio.ByteBuffer;
import java.util.ArrayList;

public class RtpH264Receiver {
    private static final int MAX_PKT = 1500;

    private DatagramSocket socket;
    private MediaCodec codec;
    private Thread rxThread;
    private volatile boolean running = false;
    private int port;
    private Surface surface;

    // FU-A 재조립 버퍼
    private ByteArrayOut fuBuf = new ByteArrayOut();
    private boolean fuActive = false;

    private byte[] sps = null, pps = null;
    private boolean codecStarted = false;

    public RtpH264Receiver(Surface s, int port) {
        this.surface = s;
        this.port = port;
    }

    public void start() {
        running = true;
        rxThread = new Thread(this::rxLoop, "rtp-rx");
        rxThread.setPriority(Thread.MAX_PRIORITY);
        rxThread.start();
    }

    public void stop() {
        running = false;
        try { if (rxThread != null) rxThread.join(500); } catch (Exception ignored) {}
        if (socket != null) socket.close();
        if (codec != null) {
            try { codec.stop(); codec.release(); } catch (Exception ignored) {}
            codec = null;
        }
        codecStarted = false;
    }

    private void rxLoop() {
        try {
            socket = new DatagramSocket(port);
            socket.setReceiveBufferSize(1 << 20);
            socket.setSoTimeout(1000);
            byte[] buf = new byte[MAX_PKT];
            DatagramPacket pkt = new DatagramPacket(buf, buf.length);

            while (running) {
                try { socket.receive(pkt); } catch (Exception e) { continue; }
                handleRtp(pkt.getData(), pkt.getLength());
                drainOutput();
            }
        } catch (Exception e) {
            android.util.Log.e("RtpH264", "rxLoop: " + e);
        }
    }

    private void handleRtp(byte[] data, int len) {
        if (len < 13) return;
        int cc = data[0] & 0x0F;
        int hdrLen = 12 + cc * 4;
        boolean ext = (data[0] & 0x10) != 0;
        if (ext) {
            if (len < hdrLen + 4) return;
            int extWords = ((data[hdrLen + 2] & 0xFF) << 8) | (data[hdrLen + 3] & 0xFF);
            hdrLen += 4 + extWords * 4;
        }
        if (len <= hdrLen) return;

        int nalType = data[hdrLen] & 0x1F;

        if (nalType >= 1 && nalType <= 23) {
            // Single NAL unit
            emitNal(data, hdrLen, len - hdrLen);
        } else if (nalType == 28) {
            // FU-A
            if (len < hdrLen + 2) return;
            byte fuInd = data[hdrLen];
            byte fuHdr = data[hdrLen + 1];
            boolean start = (fuHdr & 0x80) != 0;
            boolean end   = (fuHdr & 0x40) != 0;
            int origType  = fuHdr & 0x1F;

            if (start) {
                fuBuf.reset();
                fuBuf.write((byte) ((fuInd & 0xE0) | origType));
                fuActive = true;
            }
            if (!fuActive) return;
            fuBuf.write(data, hdrLen + 2, len - hdrLen - 2);
            if (end) {
                emitNal(fuBuf.buf(), 0, fuBuf.size());
                fuActive = false;
            }
        } else if (nalType == 24) {
            // STAP-A
            int off = hdrLen + 1;
            while (off + 2 <= len) {
                int nalSize = ((data[off] & 0xFF) << 8) | (data[off + 1] & 0xFF);
                off += 2;
                if (off + nalSize > len) break;
                emitNal(data, off, nalSize);
                off += nalSize;
            }
        }
    }

    private static final byte[] SC = {0, 0, 0, 1};

    private void emitNal(byte[] nal, int off, int size) {
        if (size <= 0) return;
        int type = nal[off] & 0x1F;

        if (type == 7) { sps = copy(nal, off, size); tryStartCodec(); return; }
        if (type == 8) { pps = copy(nal, off, size); tryStartCodec(); return; }
        if (!codecStarted) return;

        int idx;
        try { idx = codec.dequeueInputBuffer(10_000); } catch (Exception e) { return; }
        if (idx < 0) return;

        ByteBuffer ib = codec.getInputBuffer(idx);
        ib.clear();
        ib.put(SC);
        ib.put(nal, off, size);
        long pts = System.nanoTime() / 1000;
        codec.queueInputBuffer(idx, 0, size + 4, pts, 0);
    }

    private void tryStartCodec() {
        if (codecStarted || sps == null || pps == null) return;
        try {
            MediaFormat fmt = MediaFormat.createVideoFormat("video/avc", 2560, 720);
            fmt.setByteBuffer("csd-0", withSc(sps));
            fmt.setByteBuffer("csd-1", withSc(pps));
            fmt.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);   // API 30+
            fmt.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 1 << 20);

            codec = MediaCodec.createDecoderByType("video/avc");
            codec.configure(fmt, surface, null, 0);
            codec.start();
            codecStarted = true;
            android.util.Log.i("RtpH264", "codec started");
        } catch (Exception e) {
            android.util.Log.e("RtpH264", "tryStartCodec: " + e);
        }
    }

    private void drainOutput() {
        if (!codecStarted) return;
        MediaCodec.BufferInfo bi = new MediaCodec.BufferInfo();
        int outIdx;
        int lastIdx = -1;
        // 큐에 쌓인 프레임 중 최신 것만 렌더 → 지연 누적 방지
        while ((outIdx = codec.dequeueOutputBuffer(bi, 0)) >= 0) {
            if (lastIdx >= 0) codec.releaseOutputBuffer(lastIdx, false); // 오래된 건 버림
            lastIdx = outIdx;
        }
        if (lastIdx >= 0) codec.releaseOutputBuffer(lastIdx, true);      // 최신만 렌더
    }

    private static ByteBuffer withSc(byte[] nal) {
        ByteBuffer b = ByteBuffer.allocate(nal.length + 4);
        b.put(SC).put(nal);
        b.rewind();
        return b;
    }

    private static byte[] copy(byte[] s, int off, int n) {
        byte[] d = new byte[n];
        System.arraycopy(s, off, d, 0, n);
        return d;
    }

    private static class ByteArrayOut {
        private byte[] b = new byte[1 << 18];
        private int n = 0;
        void reset() { n = 0; }
        int size() { return n; }
        byte[] buf() { return b; }
        void write(byte v) { ensure(1); b[n++] = v; }
        void write(byte[] s, int off, int len) {
            if (len <= 0) return;
            ensure(len);
            System.arraycopy(s, off, b, n, len);
            n += len;
        }
        private void ensure(int extra) {
            if (n + extra <= b.length) return;
            byte[] nb = new byte[Math.max(b.length * 2, n + extra)];
            System.arraycopy(b, 0, nb, 0, n);
            b = nb;
        }
    }
}