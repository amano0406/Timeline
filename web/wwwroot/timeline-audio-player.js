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
  }
};
