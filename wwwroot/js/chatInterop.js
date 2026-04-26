window.openclawChat = window.openclawChat || {
  getMetrics: function (element) {
    if (!element) {
      return { distanceFromBottom: 0 };
    }

    const distanceFromBottom = element.scrollHeight - element.scrollTop - element.clientHeight;
    return { distanceFromBottom: distanceFromBottom };
  },

  scrollToBottom: function (element, smooth) {
    if (!element) {
      return;
    }

    const behavior = smooth ? "smooth" : "auto";
    if (typeof element.scrollTo === "function") {
      element.scrollTo({ top: element.scrollHeight, behavior: behavior });
      return;
    }

    element.scrollTop = element.scrollHeight;
  },

  getValue: function (element) {
    return element ? element.value : "";
  },

  getWindowWidth: function () {
    return window.innerWidth || document.documentElement.clientWidth || 0;
  },

  bindComposerSubmit: function (element, submitButton) {
    if (!element) {
      return;
    }

    if (element.__openclawComposerSubmit) {
      element.__openclawComposerSubmit.dispose();
    }

    const onKeyDown = function (event) {
      if (event.key !== "Enter" || event.shiftKey || event.isComposing) {
        return;
      }

      event.preventDefault();
      element.dispatchEvent(new Event("input", { bubbles: true }));
      if (submitButton && typeof submitButton.click === "function") {
        submitButton.click();
      }
    };

    element.addEventListener("keydown", onKeyDown);
    element.__openclawComposerSubmit = {
      dispose: function () {
        element.removeEventListener("keydown", onKeyDown);
      }
    };
  }
};
