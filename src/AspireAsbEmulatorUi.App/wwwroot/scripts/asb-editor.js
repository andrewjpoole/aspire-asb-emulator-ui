window.asbEditor = {
  focusLastAppPropKey: function () {
    try {
      const inputs = document.querySelectorAll('.app-prop-key');
      if (inputs && inputs.length) {
        const last = inputs[inputs.length - 1];
        last.focus();
        if (last.select) last.select();
      }
    } catch (e) {
      // ignore
    }
  }
};
