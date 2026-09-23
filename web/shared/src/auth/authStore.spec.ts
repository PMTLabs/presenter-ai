import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearAuthSession, refreshAuth, setAuthSession, useAuthStore } from "./authStore";

const user = { id: "usr_test", email: "test@example.invalid", displayName: null, role: "user" };

describe("auth readiness", () => {
  beforeEach(() => {
    clearAuthSession();
    useAuthStore.setState({ ready: false });
  });

  afterEach(() => {
    clearAuthSession();
    vi.unstubAllGlobals();
  });

  it("becomes ready after a failed startup refresh", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 401 })));

    await expect(refreshAuth()).resolves.toBe(false);

    expect(useAuthStore.getState()).toMatchObject({ ready: true, user: null });
  });

  it("is ready after sign-in and sign-out", async () => {
    setAuthSession({ accessToken: "access-token", expiresAt: "2099-01-01T00:00:00Z", user });
    expect(useAuthStore.getState()).toMatchObject({ ready: true, user });
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 204 })));

    await useAuthStore.getState().signOut();

    expect(useAuthStore.getState()).toMatchObject({ ready: true, user: null });
  });
});
