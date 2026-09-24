import { useEffect, useRef, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import apiClient from "@presenter/shared/api";
import { errorMessages, isProblem } from "@presenter/shared";

export function ToolsOAuthCallback() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const started = useRef(false);
  const [message, setMessage] = useState("Completing tool authorization…");
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    const code = params.get("code");
    const state = params.get("state");
    if (!code || !state) {
      setFailed(true);
      setMessage("Tool authorization could not be completed. Please try again.");
      return;
    }

    void apiClient
      .POST("/v1/tools/oauth/complete", {
        body: {
          code,
          state,
          ...(params.get("iss") ? { iss: params.get("iss")! } : {}),
        },
      })
      .then(({ error }) => {
        if (error) {
          setFailed(true);
          setMessage(
            isProblem(error)
              ? (errorMessages[error.code] ??
                error.detail ??
                error.title ??
                "Tool authorization failed.")
              : "Tool authorization failed.",
          );
          return;
        }
        setMessage("Tool server connected successfully.");
        window.setTimeout(
          () =>
            navigate("/tools", {
              replace: true,
              state: { toolsNotice: "Tool server connected successfully." },
            }),
          900,
        );
      })
      .catch(() => {
        setFailed(true);
        setMessage("Tool authorization failed.");
      });
  }, [navigate, params]);

  return (
    <div
      role={failed ? "alert" : "status"}
      className={
        failed
          ? "p-8 text-red-700 dark:text-red-200"
          : "p-8 text-gray-700 dark:text-gray-200"
      }
    >
      {message}
    </div>
  );
}
