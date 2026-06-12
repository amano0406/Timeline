window.timelineDownload = (() => {
  const handles = new Map();

  function safeFileName(name) {
    const value = String(name || "timeline-download.zip").trim();
    return value.replace(/[\\/:*?"<>|\r\n]+/g, "_") || "timeline-download.zip";
  }

  async function beginSave(defaultFileName) {
    const id = crypto.randomUUID();
    const suggestedName = safeFileName(defaultFileName);
    if (!window.showSaveFilePicker) {
      handles.set(id, null);
      return { id, accepted: true, message: "" };
    }

    try {
      const handle = await window.showSaveFilePicker({
        suggestedName,
        types: [
          {
            description: "ZIP archive",
            accept: { "application/zip": [".zip"] },
          },
        ],
      });
      handles.set(id, handle);
      return { id, accepted: true, message: "" };
    } catch (error) {
      if (error && error.name === "AbortError") {
        return { id: "", accepted: false, message: "" };
      }

      const message = error && error.message ? error.message : "";
      if (
        error &&
        error.name === "NotAllowedError" &&
        /user gesture|user activation|gesture/i.test(message)
      ) {
        handles.set(id, null);
        return { id, accepted: true, message: "" };
      }

      return {
        id: "",
        accepted: false,
        message: message || "保存先を選択できませんでした。",
      };
    }
  }

  async function saveUrl(id, url, fallbackFileName) {
    const response = await fetch(url, { cache: "no-store" });
    if (!response.ok) {
      throw new Error(`ダウンロードできませんでした。HTTP ${response.status}`);
    }

    const blob = await response.blob();
    const handle = handles.get(id);
    handles.delete(id);
    if (!blob || blob.size <= 0) {
      throw new Error("ダウンロード ZIP が空でした。スキャン後にもう一度試してください。");
    }

    if (handle) {
      const writable = await handle.createWritable();
      await writable.write(blob);
      await writable.close();
      return true;
    }

    const objectUrl = URL.createObjectURL(blob);
    try {
      const anchor = document.createElement("a");
      anchor.href = objectUrl;
      anchor.download = safeFileName(fallbackFileName);
      anchor.style.display = "none";
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
    } finally {
      setTimeout(() => URL.revokeObjectURL(objectUrl), 30000);
    }
    return true;
  }

  return { beginSave, saveUrl };
})();
