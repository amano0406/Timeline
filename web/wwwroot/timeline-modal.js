(function () {
  const enhancedDialogs = new WeakSet();

  function clamp(value, min, max) {
    return Math.min(Math.max(value, min), max);
  }

  function enhanceDialog(dialog) {
    if (!dialog || enhancedDialogs.has(dialog)) {
      return;
    }

    const handle = dialog.querySelector("[data-timeline-modal-drag-handle]");
    if (!handle) {
      return;
    }

    enhancedDialogs.add(dialog);
    const root = dialog.closest("[data-timeline-modal-root]");
    if (root) {
      window.setTimeout(function () {
        try {
          root.focus({ preventScroll: true });
        } catch (_) {
          root.focus();
        }
      }, 0);
    }

    let dragging = false;
    let offsetX = 0;
    let offsetY = 0;

    handle.addEventListener("pointerdown", function (event) {
      if (event.button !== 0) {
        return;
      }

      if (event.target && event.target.closest("button,a,input,select,textarea")) {
        return;
      }

      const rect = dialog.getBoundingClientRect();
      dragging = true;
      offsetX = event.clientX - rect.left;
      offsetY = event.clientY - rect.top;

      dialog.style.position = "fixed";
      dialog.style.left = rect.left + "px";
      dialog.style.top = rect.top + "px";
      dialog.style.margin = "0";
      dialog.style.width = rect.width + "px";
      dialog.style.height = rect.height + "px";
      dialog.style.maxWidth = "none";
      dialog.style.zIndex = "1060";
      dialog.classList.add("tfa-twe-modal-dialog-dragging");

      handle.setPointerCapture(event.pointerId);
      event.preventDefault();
    });

    handle.addEventListener("pointermove", function (event) {
      if (!dragging) {
        return;
      }

      const rect = dialog.getBoundingClientRect();
      const maxLeft = Math.max(0, window.innerWidth - rect.width);
      const maxTop = Math.max(0, window.innerHeight - 56);
      const left = clamp(event.clientX - offsetX, 0, maxLeft);
      const top = clamp(event.clientY - offsetY, 0, maxTop);

      dialog.style.left = left + "px";
      dialog.style.top = top + "px";
    });

    handle.addEventListener("pointerup", function (event) {
      dragging = false;
      dialog.classList.remove("tfa-twe-modal-dialog-dragging");
      if (handle.hasPointerCapture(event.pointerId)) {
        handle.releasePointerCapture(event.pointerId);
      }
    });

    handle.addEventListener("pointercancel", function (event) {
      dragging = false;
      dialog.classList.remove("tfa-twe-modal-dialog-dragging");
      if (handle.hasPointerCapture(event.pointerId)) {
        handle.releasePointerCapture(event.pointerId);
      }
    });
  }

  window.timelineModal = window.timelineModal || {};
  window.timelineModal.enhance = function () {
    document
      .querySelectorAll("[data-timeline-modal-dialog]")
      .forEach(enhanceDialog);
  };

  document.addEventListener("DOMContentLoaded", function () {
    window.timelineModal.enhance();
  });
})();
