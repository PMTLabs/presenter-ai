export const DEV_TICKET = "dev";

export const devUser = {
  id: "dev-user",
  email: "dev@presenter-ai.local",
  displayName: "Dev user",
};

/** Development-only identity used until real session tickets are introduced. */
export function signInDev() {
  return devUser;
}
