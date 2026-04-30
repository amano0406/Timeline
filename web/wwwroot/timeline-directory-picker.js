const timelineDirectoryPicker = {
  async pick(title, initialPath) {
    const params = new URLSearchParams({
      title: title || "Select directory",
      initialPath: initialPath || "",
    });

    let response;
    try {
      response = await fetch(`http://127.0.0.1:19001/pick-directory?${params.toString()}`, {
        method: "GET",
        cache: "no-store",
      });
    } catch {
      throw new Error("ディレクトリ選択を起動できません。start.bat から Timeline を起動してください。");
    }

    if (!response.ok) {
      throw new Error("ディレクトリ選択を起動できませんでした。");
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
