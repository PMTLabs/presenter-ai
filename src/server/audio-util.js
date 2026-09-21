// Small PCM16 helpers used to tell speech from the silent frames GPT-Live streams continuously.

/** RMS amplitude of PCM16 LE mono samples (0..32767). Subsamples for speed. */
export function pcmRms(buffer, stride = 2) {
  const samples = Math.floor(buffer.length / 2);
  if (samples === 0) return 0;
  let sum = 0;
  let n = 0;
  for (let i = 0; i < samples; i += stride) {
    const v = buffer.readInt16LE(i * 2);
    sum += v * v;
    n++;
  }
  return n ? Math.sqrt(sum / n) : 0;
}

/**
 * Default threshold: the live service sends exact digital silence (RMS 0) between utterances;
 * quiet speech measured around RMS 150–800, loud speech 1000–4000.
 */
export const VOICE_RMS_THRESHOLD = 120;

export function isVoiced(buffer, threshold = VOICE_RMS_THRESHOLD) {
  return pcmRms(buffer) >= threshold;
}
