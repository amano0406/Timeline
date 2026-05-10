(function () {
  const minCardHeight = 280;
  const fallbackBottomGap = 16;
  let pending = false;

  function refreshListCard(card) {
    const rect = card.getBoundingClientRect();
    const container = card.closest(".tfa-main") || document.body;
    const containerStyle = window.getComputedStyle(container);
    const containerBottomPadding = Number.parseFloat(containerStyle.paddingBottom) || 0;
    const bottomGap = Math.max(fallbackBottomGap, containerBottomPadding);
    const availableHeight = Math.floor(window.innerHeight - rect.top - bottomGap);
    if (availableHeight < minCardHeight) {
      card.style.removeProperty("height");
      card.style.removeProperty("max-height");
      return;
    }

    card.style.height = availableHeight + "px";
    card.style.maxHeight = availableHeight + "px";
  }

  function refresh() {
    pending = false;
    document.querySelectorAll(".tfa-list-card").forEach(refreshListCard);
  }

  function scheduleRefresh() {
    if (pending) {
      return;
    }

    pending = true;
    window.requestAnimationFrame(refresh);
  }

  window.timelineListLayout = window.timelineListLayout || {};
  window.timelineListLayout.refresh = scheduleRefresh;

  window.addEventListener("resize", scheduleRefresh);
  document.addEventListener("DOMContentLoaded", scheduleRefresh);

  const observer = new MutationObserver(scheduleRefresh);
  observer.observe(document.documentElement, {
    childList: true,
    subtree: true,
  });
})();
