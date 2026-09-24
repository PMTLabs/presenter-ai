export const END_REASON_TEXT: Record<string, string> = {
  user: "Talk ended by user",
  completed: "Talk completed",
  max_length: "Maximum talk length reached",
  idle: "Ended due to inactivity",
  heartbeat: "Connection lost (heartbeat timeout)",
  writer_failed: "Connection failed",
  backpressure: "Connection closed due to backpressure",
  disconnect: "Disconnected",
  takeover: "Taken over in another tab",
  shutdown: "Server shutting down",
  error: "An error occurred",
  upstream_lost: "Upstream connection lost",
  reconnect_failed: "Failed to reconnect",
  cli_cancelled: "Cancelled from CLI",
  cli_max_seconds: "Maximum duration reached",
  stop_after_slide: "Stopped after slide",
};

export function formatEndReason(endReason: string | null | undefined): string | null {
  if (!endReason) return null;
  return END_REASON_TEXT[endReason] ?? endReason;
}
