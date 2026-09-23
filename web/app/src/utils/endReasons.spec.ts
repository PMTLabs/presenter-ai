import { describe, expect, it } from "vitest";
import { END_REASON_TEXT, formatEndReason } from "./endReasons";

describe("formatEndReason", () => {
  it("returns null for null or empty reason", () => {
    expect(formatEndReason(null)).toBeNull();
    expect(formatEndReason(undefined)).toBeNull();
    expect(formatEndReason("")).toBeNull();
    expect(END_REASON_TEXT.user).toBe("Talk ended by user");
  });

  it("maps closed vocabulary to plain words", () => {
    expect(formatEndReason("max_length")).toBe("Maximum talk length reached");
    expect(formatEndReason("idle")).toBe("Ended due to inactivity");
    expect(formatEndReason("user")).toBe("Talk ended by user");
    expect(formatEndReason("completed")).toBe("Talk completed");
    expect(formatEndReason("heartbeat")).toBe("Connection lost (heartbeat timeout)");
    expect(formatEndReason("disconnect")).toBe("Disconnected");
    expect(formatEndReason("takeover")).toBe("Taken over in another tab");
    expect(formatEndReason("shutdown")).toBe("Server shutting down");
    expect(formatEndReason("error")).toBe("An error occurred");
    expect(formatEndReason("writer_failed")).toBe("Connection failed");
    expect(formatEndReason("backpressure")).toBe("Connection closed due to backpressure");
    expect(formatEndReason("upstream_lost")).toBe("Upstream connection lost");
    expect(formatEndReason("reconnect_failed")).toBe("Failed to reconnect");
    expect(formatEndReason("cli_cancelled")).toBe("Cancelled from CLI");
    expect(formatEndReason("cli_max_seconds")).toBe("Maximum duration reached");
    expect(formatEndReason("stop_after_slide")).toBe("Stopped after slide");
  });

  it("returns raw reason for unknown string", () => {
    expect(formatEndReason("custom_reason")).toBe("custom_reason");
  });
});
