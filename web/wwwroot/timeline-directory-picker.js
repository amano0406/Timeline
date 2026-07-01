const timelineDirectoryPicker = {
  localApiBaseUrl() {
    return (window.timelineLocalApiBaseUrl || "http://127.0.0.1:19001").replace(/\/+$/, "");
  },

  async pick(title, initialPath) {
    const params = new URLSearchParams({
      title: title || "Select directory",
      initialPath: initialPath || "",
    });

    let response;
    try {
      response = await fetch(`${this.localApiBaseUrl()}/pick-directory?${params.toString()}`, {
        method: "GET",
        cache: "no-store",
      });
    } catch {
      throw new Error("ディレクトリ選択を起動できません。Timeline Launcher から Timeline を起動してください。");
    }

    if (!response.ok) {
      const message = await this.readErrorMessage(response);
      return this.promptForPath("ディレクトリのパスを入力してください。", initialPath, message);
    }

    const payload = await response.json();
    if (payload.cancelled) {
      return null;
    }
    if (!payload.path) {
      throw new Error("ディレクトリを選択できませんでした。");
    }
    return payload.path;
  },

  async pickFile(title, initialPath, filter) {
    const params = new URLSearchParams({
      title: title || "Select file",
      initialPath: initialPath || "",
      filter: filter || "All files (*.*)|*.*",
    });

    let response;
    try {
      response = await fetch(`${this.localApiBaseUrl()}/pick-file?${params.toString()}`, {
        method: "GET",
        cache: "no-store",
      });
    } catch {
      throw new Error("ファイル選択を起動できません。Timeline Launcher から Timeline を起動してください。");
    }

    if (!response.ok) {
      const message = await this.readErrorMessage(response);
      return this.promptForPath("ファイルのパスを入力してください。", initialPath, message);
    }

    const payload = await response.json();
    if (payload.cancelled) {
      return null;
    }
    if (!payload.path) {
      throw new Error("ファイルを選択できませんでした。");
    }
    return payload.path;
  },

  async readErrorMessage(response) {
    try {
      const payload = await response.json();
      if (payload && payload.message) {
        return payload.message;
      }
    } catch {
      // Fall through to the generic fallback below.
    }
    return "選択ダイアログを起動できませんでした。";
  },

  promptForPath(label, initialPath, reason) {
    const message = reason ? `${reason}\n\n${label}` : label;
    const value = window.prompt(message, initialPath || "");
    if (value === null) {
      return null;
    }

    const trimmed = value.trim();
    return trimmed.length > 0 ? trimmed : null;
  },
};

window.timelineDirectoryPicker = timelineDirectoryPicker;
window.timelineForAudioDirectoryPicker = timelineDirectoryPicker;
window.timelineForAudioPlayer = {
  seek(id, seconds) {
    const element = document.getElementById(id);
    if (!element) {
      return;
    }
    element.currentTime = seconds || 0;
    element.play();
  },
};

window.timelineFileActions = {
  downloadJson(fileName, payload) {
    const blob = new Blob([JSON.stringify(payload, null, 2)], {
      type: "application/json;charset=utf-8",
    });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName || "timeline-selection.json";
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  },
};
