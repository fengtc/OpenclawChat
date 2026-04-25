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
  }
};
