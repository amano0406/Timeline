window.timelineAudioPlayer = {
  seek: (audioId, seconds) => {
    const audio = document.getElementById(audioId);
    if (!audio) {
      return;
    }

    const value = Number(seconds);
    if (Number.isFinite(value) && value >= 0) {
      audio.currentTime = value;
    }
    audio.play();
  },
  watch: (audioId, dotNetRef) => {
    const audio = document.getElementById(audioId);
    if (!audio || !dotNetRef) {
      return;
    }

    window.timelineAudioPlayer.unwatch(audioId);
    let lastSecond = -1;
    const handler = () => {
      const current = Number(audio.currentTime);
      if (!Number.isFinite(current)) {
        return;
      }

      const rounded = Math.floor(current * 2) / 2;
      if (rounded === lastSecond) {
        return;
      }

      lastSecond = rounded;
      dotNetRef.invokeMethodAsync("OnAudioTimeChanged", current).catch(() => {});
    };
    audio.addEventListener("timeupdate", handler);
    audio.addEventListener("seeked", handler);
    audio.dataset.timelineWatchAttached = "true";
    audio._timelineAudioWatchHandler = handler;
  },
  unwatch: (audioId) => {
    const audio = document.getElementById(audioId);
    if (!audio || !audio._timelineAudioWatchHandler) {
      return;
    }

    audio.removeEventListener("timeupdate", audio._timelineAudioWatchHandler);
    audio.removeEventListener("seeked", audio._timelineAudioWatchHandler);
    delete audio._timelineAudioWatchHandler;
    delete audio.dataset.timelineWatchAttached;
  }
};
