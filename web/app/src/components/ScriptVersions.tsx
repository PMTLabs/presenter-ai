import { useCallback, useEffect, useState } from "react";
import apiClient, { type components } from "@presenter/shared/api";
import { errorMessages, isProblem } from "@presenter/shared";
import { usePresenterStore } from "../store/presenterStore";

type RevisionSummary = components["schemas"]["RevisionSummary"];
type RevisionDetail = components["schemas"]["RevisionDetail"];
type PendingEdit = components["schemas"]["PendingEditDto"];

export type ScriptVersionsProps = {
  presentationId: string;
};

function describeError(problem: unknown, fallback: string): string {
  return isProblem(problem)
    ? (errorMessages[problem.code] ?? problem.detail ?? problem.title ?? fallback)
    : fallback;
}

function formatSlideIndexes(indexes: readonly (number | string)[]): string {
  return indexes.map((index) => `slide ${Number(index) + 1}`).join(", ");
}

function formatTime(createdAt: string): string {
  const parsed = new Date(createdAt);
  return Number.isNaN(parsed.getTime()) ? createdAt : parsed.toLocaleString();
}

/**
 * Script versions panel: lists every revision with source/time/summary, shows per-slide before/after for the
 * selected version, and reverts to an older version. Works while idle and while presenting; refreshes whenever
 * the store's `scriptVersion` moves (the presenter announces a `script_version` frame after every applied commit).
 */
export function ScriptVersions({ presentationId }: ScriptVersionsProps) {
  const scriptVersion = usePresenterStore((s) => s.scriptVersion);
  const edits = usePresenterStore((s) => s.edits);
  const [versions, setVersions] = useState<RevisionSummary[]>([]);
  const [listError, setListError] = useState<string | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  const [detail, setDetail] = useState<RevisionDetail | null>(null);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [reverting, setReverting] = useState(false);
  const [revertError, setRevertError] = useState<string | null>(null);
  const [pendingNotice, setPendingNotice] = useState<{
    number: number;
    pendingEdits: PendingEdit[];
  } | null>(null);

  const loadVersions = useCallback(async () => {
    if (!presentationId) return;
    try {
      const { data, error } = await apiClient.GET("/v1/presentations/{id}/revisions", {
        params: { path: { id: presentationId } },
      });
      if (error) {
        setListError(describeError(error, "Unable to load script versions."));
        return;
      }
      setListError(null);
      if (data) setVersions(Array.isArray(data.items) ? data.items : []);
    } catch {
      setListError("Unable to load script versions.");
    }
  }, [presentationId]);

  useEffect(() => {
    setSelected(null);
    setDetail(null);
    setDetailError(null);
    setPendingNotice(null);
    void loadVersions();
  }, [loadVersions]);

  // Refresh on the presenter's script_version frame (Start, toggle, reconnect, every applied commit).
  useEffect(() => {
    if (scriptVersion === null) return;
    void loadVersions();
  }, [scriptVersion, loadVersions]);

  useEffect(() => {
    if (selected === null || !presentationId) {
      setDetail(null);
      return;
    }
    let active = true;
    setDetailError(null);
    void apiClient
      .GET("/v1/presentations/{id}/revisions/{number}", {
        params: { path: { id: presentationId, number: selected } },
      })
      .then(({ data, error }) => {
        if (!active) return;
        if (error) {
          setDetail(null);
          setDetailError(describeError(error, "Unable to load that version."));
          return;
        }
        if (data) setDetail(data);
      })
      .catch(() => {
        if (active) setDetailError("Unable to load that version.");
      });
    return () => {
      active = false;
    };
  }, [presentationId, selected]);

  async function revert(number: number) {
    if (!presentationId || reverting) return;
    setReverting(true);
    setRevertError(null);
    try {
      const { data, error } = await apiClient.POST(
        "/v1/presentations/{id}/revisions/{number}/revert",
        { params: { path: { id: presentationId, number } } },
      );
      if (error) {
        setRevertError(describeError(error, "Unable to revert."));
        return;
      }
      if (data) {
        setPendingNotice({ number: Number(data.revision.number), pendingEdits: data.pendingEdits });
        setSelected(Number(data.revision.number));
      }
      void loadVersions();
    } catch {
      setRevertError("Unable to revert.");
    } finally {
      setReverting(false);
    }
  }

  return (
    <section className="min-h-0 flex-none rounded-xl border border-gray-200 bg-white p-3 text-gray-900 dark:border-gray-800 dark:bg-gray-900 dark:text-gray-100">
      <h2 className="mb-2 text-sm font-semibold">Script versions</h2>
      {listError && (
        <p className="mb-2 rounded-lg bg-red-50 p-2 text-xs text-red-700 dark:bg-red-950 dark:text-red-200">
          {listError}
        </p>
      )}
      {revertError && (
        <p className="mb-2 rounded-lg bg-red-50 p-2 text-xs text-red-700 dark:bg-red-950 dark:text-red-200">
          {revertError}
        </p>
      )}
      {pendingNotice && pendingNotice.pendingEdits.length > 0 && (
        <p
          role="status"
          className="mb-2 rounded-lg bg-amber-50 p-2 text-xs text-amber-800 dark:bg-amber-950 dark:text-amber-200"
        >
          {pendingNotice.pendingEdits.length} pending edit
          {pendingNotice.pendingEdits.length === 1 ? "" : "s"} will apply after this revert:{" "}
          {pendingNotice.pendingEdits
            .map((edit) => {
              const status = edits[edit.id]?.status ?? edit.status;
              return `${edit.id} (${formatSlideIndexes(edit.slideIndexes)}, ${status})`;
            })
            .join(", ")}
        </p>
      )}
      <ul className="max-h-48 overflow-auto text-xs">
        {versions.length === 0 && !listError && (
          <li className="text-gray-500 dark:text-gray-400">No versions yet.</li>
        )}
        {versions.map((version) => {
          const number = Number(version.number);
          return (
            <li key={number}>
              <button
                type="button"
                onClick={() => setSelected(number)}
                className={[
                  "mb-1 flex w-full items-center justify-between gap-2 rounded-lg px-2 py-1 text-left",
                  selected === number
                    ? "bg-blue-100 text-blue-900 dark:bg-blue-900 dark:text-blue-100"
                    : "hover:bg-gray-100 dark:hover:bg-gray-800",
                ].join(" ")}
              >
                <span className="font-medium">
                  v{number}
                  {version.isCurrent ? " (current)" : ""}
                </span>
                <span className="text-gray-500 dark:text-gray-400">{version.source}</span>
                <span className="flex-1 truncate">{version.summary}</span>
                <span className="text-gray-500 dark:text-gray-400">{formatTime(version.createdAt)}</span>
              </button>
            </li>
          );
        })}
      </ul>
      {detailError && (
        <p className="mt-2 rounded-lg bg-red-50 p-2 text-xs text-red-700 dark:bg-red-950 dark:text-red-200">
          {detailError}
        </p>
      )}
      {detail && (
        <div className="mt-2 border-t border-gray-200 pt-2 dark:border-gray-800">
          <div className="mb-2 flex items-center justify-between">
            <span className="text-xs font-semibold">v{Number(detail.number)} changes</span>
            {!detail.isCurrent && (
              <button
                type="button"
                onClick={() => void revert(Number(detail.number))}
                disabled={reverting}
                className="rounded-lg bg-blue-600 px-2 py-1 text-xs font-medium text-white hover:bg-blue-500 disabled:cursor-not-allowed disabled:opacity-40"
              >
                {reverting ? "Reverting…" : "Revert to this version"}
              </button>
            )}
          </div>
          {detail.changes.length === 0 && (
            <p className="text-xs text-gray-500 dark:text-gray-400">No slide changes recorded.</p>
          )}
          {detail.changes.map((change) => (
            <div key={Number(change.slideIndex)} className="mb-2 text-xs">
              <p className="font-medium">
                Slide {Number(change.slideIndex) + 1}: {change.title}
              </p>
              <p className="text-gray-500 dark:text-gray-400">Before: {change.before ?? "—"}</p>
              <p className="text-gray-500 dark:text-gray-400">After: {change.after ?? "—"}</p>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}
