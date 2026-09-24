window.qoderHelper = {
  openInNewTab: function (url) {
    if (!url) return;
    try {
      const win = window.open(url, '_blank');
      if (win) {
        win.focus();
      } else {
        // In case popup blocker intercepts, fallback to navigating or alert
        window.location.href = url;
      }
    } catch (e) {
      console.error("Failed to open tab:", e);
    }
  },
  copyText: async function (text) {
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        await navigator.clipboard.writeText(text);
        return true;
      }
      // Fallback
      const textArea = document.createElement("textarea");
      textArea.value = text;
      textArea.style.position = "fixed";
      textArea.style.left = "-999999px";
      document.body.appendChild(textArea);
      textArea.focus();
      textArea.select();
      document.execCommand('copy');
      textArea.remove();
      return true;
    } catch (err) {
      console.error('Could not copy text: ', err);
      return false;
    }
  },
  scrollToBottom: function (elementId) {
    const el = document.getElementById(elementId);
    if (el) {
      el.scrollTop = el.scrollHeight;
    }
  }
};
