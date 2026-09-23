export type EchoReference = {
  destination: MediaStreamAudioDestinationNode;
  element: HTMLAudioElement;
  close: () => void;
};

type Options = {
  context: AudioContext;
  onReference: (reference: "loopback" | "fallback", reason?: string) => void;
  onFallback: () => void;
};

/**
 * Routes Web Audio playout through a local WebRTC receive track, which Chromium
 * can use as its acoustic echo-canceller reference. This function creates and
 * starts the audio element synchronously, while its caller is still in the
 * Start click's activation.
 */
export function createEchoReference(options: Options): EchoReference | null {
  const PeerConnection = window.RTCPeerConnection;
  if (!PeerConnection) {
    fallback(options, "RTCPeerConnection is unavailable");
    return null;
  }

  const element = document.createElement("audio");
  element.autoplay = true;
  element.setAttribute("playsinline", "");
  // Start playback before any negotiation awaits, while the click activation is live.
  void element.play().catch(() => undefined);

  let source: RTCPeerConnection | null = null;
  let receiver: RTCPeerConnection | null = null;
  let destination: MediaStreamAudioDestinationNode | null = null;
  let timeout: ReturnType<typeof setTimeout> | undefined;
  let settled = false;
  let closed = false;
  const streams = new Set<MediaStream>();

  const close = () => {
    if (closed) return;
    closed = true;
    if (timeout) clearTimeout(timeout);
    const attached = element.srcObject as MediaStream | null;
    if (attached) streams.add(attached);
    for (const stream of streams) stream.getTracks().forEach((track) => track.stop());
    destination?.stream.getTracks().forEach((track) => track.stop());
    source?.close();
    receiver?.close();
    element.srcObject = null;
  };

  const fail = (reason: string) => {
    if (settled || closed) return;
    settled = true;
    close();
    fallback(options, reason);
  };

  try {
    destination = options.context.createMediaStreamDestination();
    source = new PeerConnection({ iceServers: [] });
    receiver = new PeerConnection({ iceServers: [] });
    const sourceCandidates: RTCIceCandidate[] = [];
    const receiverCandidates: RTCIceCandidate[] = [];
    let receiverRemoteReady = false;
    let sourceRemoteReady = false;
    const sendCandidate = (peer: RTCPeerConnection, candidate: RTCIceCandidate) =>
      void peer.addIceCandidate(candidate).catch(() => fail("candidate exchange failed"));
    source.onicecandidate = (event) => {
      if (!event.candidate) return;
      if (receiverRemoteReady) sendCandidate(receiver!, event.candidate);
      else sourceCandidates.push(event.candidate);
    };
    receiver.onicecandidate = (event) => {
      if (!event.candidate) return;
      if (sourceRemoteReady) sendCandidate(source!, event.candidate);
      else receiverCandidates.push(event.candidate);
    };
    receiver.ontrack = (event) => {
      if (closed) return;
      const stream = event.streams[0] ?? new MediaStream([event.track]);
      streams.add(stream);
      element.srcObject = stream;
    };
    for (const track of destination.stream.getAudioTracks()) source.addTrack(track, destination.stream);

    element.addEventListener(
      "playing",
      () => {
        if (settled || closed) return;
        settled = true;
        if (timeout) clearTimeout(timeout);
        options.onReference("loopback");
      },
      { once: true },
    );
    timeout = setTimeout(() => fail("loopback audio did not start within 2 s"), 2000);
    void negotiate(source, receiver, () => {
      receiverRemoteReady = true;
      sourceCandidates.splice(0).forEach((candidate) => sendCandidate(receiver!, candidate));
    }, () => {
      sourceRemoteReady = true;
      receiverCandidates.splice(0).forEach((candidate) => sendCandidate(source!, candidate));
    }).catch(() => fail("loopback negotiation failed"));
    return { destination, element, close };
  } catch {
    // Variables have only been assigned after their constructors succeed.
    if (!closed) {
      closed = true;
      if (timeout) clearTimeout(timeout);
      try {
        source?.close();
        receiver?.close();
      } catch {
        // Best-effort cleanup of a partial setup.
      }
      element.srcObject = null;
    }
    fallback(options, "loopback setup failed");
    return null;
  }
}

async function negotiate(
  source: RTCPeerConnection,
  receiver: RTCPeerConnection,
  onReceiverReady: () => void,
  onSourceReady: () => void,
) {
  const offer = await source.createOffer();
  await source.setLocalDescription(mungeOpus(offer));
  const localOffer = source.localDescription;
  if (!localOffer) throw new Error("loopback offer was not set");
  await receiver.setRemoteDescription(localOffer);
  onReceiverReady();
  const answer = await receiver.createAnswer();
  await receiver.setLocalDescription(mungeOpus(answer));
  const localAnswer = receiver.localDescription;
  if (!localAnswer) throw new Error("loopback answer was not set");
  await source.setRemoteDescription(localAnswer);
  onSourceReady();
}

/** Keeps the negotiated local loopback mono Opus, without DTX, near 96 kbit/s. */
export function mungeOpus(description: RTCSessionDescriptionInit): RTCSessionDescriptionInit {
  if (!description.sdp) return description;
  const lines = description.sdp.split("\r\n");
  const rtpmap = lines.findIndex((line) => /^a=rtpmap:(\d+) opus\/48000\/2$/i.test(line));
  const payloadType = lines[rtpmap]?.match(/^a=rtpmap:(\d+)/)?.[1];
  if (!payloadType) return description;
  const prefix = `a=fmtp:${payloadType} `;
  const params = "stereo=0;sprop-stereo=0;usedtx=0;maxaveragebitrate=96000;cbr=1";
  const index = lines.findIndex((line) => line.startsWith(prefix));
  if (index >= 0) lines[index] = `${lines[index]};${params}`;
  // Inside the media section, right after its rtpmap, not after the SDP's trailing empty line.
  else lines.splice(rtpmap + 1, 0, `${prefix}${params}`);
  return { ...description, sdp: lines.join("\r\n") };
}

function fallback(options: Options, reason: string) {
  options.onFallback();
  options.onReference("fallback", reason);
}
