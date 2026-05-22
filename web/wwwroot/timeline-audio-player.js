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
  pause: (audioId) => {
    const audio = document.getElementById(audioId);
    if (!audio) {
      return;
    }

    audio.pause();
  },
  scrollTurnIntoView: (scrollElementId, startValue) => {
    const container = document.getElementById(scrollElementId);
    if (!container) {
      return;
    }

    const key = String(startValue);
    const escapedKey = window.CSS && CSS.escape
      ? CSS.escape(key)
      : key.replace(/["\\]/g, "\\$&");
    const row = container.querySelector(`[data-turn-start="${escapedKey}"]`);
    if (!row) {
      return;
    }

    const containerRect = container.getBoundingClientRect();
    const rowRect = row.getBoundingClientRect();
    const headerOffset = 44;
    const isAbove = rowRect.top < containerRect.top + headerOffset;
    const isBelow = rowRect.bottom > containerRect.bottom;
    if (!isAbove && !isBelow) {
      return;
    }

    const nextTop = container.scrollTop
      + rowRect.top
      - containerRect.top
      - Math.max(headerOffset, container.clientHeight * 0.28);
    container.scrollTo({
      top: Math.max(0, nextTop),
      behavior: "smooth"
    });
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
    const stateHandler = () => {
      dotNetRef.invokeMethodAsync("OnAudioPlaybackStateChanged", !audio.paused && !audio.ended).catch(() => {});
    };
    audio.addEventListener("timeupdate", handler);
    audio.addEventListener("seeked", handler);
    audio.addEventListener("play", stateHandler);
    audio.addEventListener("pause", stateHandler);
    audio.addEventListener("ended", stateHandler);
    audio.dataset.timelineWatchAttached = "true";
    audio._timelineAudioWatchHandler = handler;
    audio._timelineAudioStateHandler = stateHandler;
  },
  unwatch: (audioId) => {
    const audio = document.getElementById(audioId);
    if (!audio || !audio._timelineAudioWatchHandler) {
      return;
    }

    audio.removeEventListener("timeupdate", audio._timelineAudioWatchHandler);
    audio.removeEventListener("seeked", audio._timelineAudioWatchHandler);
    if (audio._timelineAudioStateHandler) {
      audio.removeEventListener("play", audio._timelineAudioStateHandler);
      audio.removeEventListener("pause", audio._timelineAudioStateHandler);
      audio.removeEventListener("ended", audio._timelineAudioStateHandler);
    }
    delete audio._timelineAudioWatchHandler;
    delete audio._timelineAudioStateHandler;
    delete audio.dataset.timelineWatchAttached;
  }
};
