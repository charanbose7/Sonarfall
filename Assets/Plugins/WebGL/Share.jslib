// Share.jslib — the WebGL half of ShareUtil.ShareText.
//
// Result codes passed back to C#: 1 shared (a share sheet took it), 2 copied to the clipboard,
// 3 cancelled (the player closed the share sheet), 0 failed.
//
// Order of attempts:
//   1. navigator.share — mobile browsers only. Desktop Chrome on Windows also exposes it, but
//      there it opens the OS share dialog, which is not what "share" means on a desktop page.
//   2. navigator.clipboard.writeText — needs a secure context (HTTPS or localhost).
//   3. document.execCommand('copy') on a hidden textarea — the old path, for plain-HTTP hosts.
// Both 1 and 2 need a user gesture; ShareUtil is only ever called from a button press, which is
// inside the browser's transient-activation window.
mergeInto(LibraryManager.library, {
  SonarfallShareText: function (textPtr, callback) {
    var text = UTF8ToString(textPtr);
    var reported = false;
    function done(code) {
      if (reported) return;
      reported = true;
      {{{ makeDynCall('vi', 'callback') }}}(code);
    }
    function legacyCopy() {
      try {
        var ta = document.createElement('textarea');
        ta.value = text;
        ta.setAttribute('readonly', '');
        ta.style.position = 'fixed';
        ta.style.top = '0';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        var ok = document.execCommand('copy');
        document.body.removeChild(ta);
        return !!ok;
      } catch (e) {
        return false;
      }
    }
    function copy() {
      if (navigator.clipboard && window.isSecureContext) {
        navigator.clipboard.writeText(text).then(
          function () { done(2); },
          function () { done(legacyCopy() ? 2 : 0); });
      } else {
        done(legacyCopy() ? 2 : 0);
      }
    }
    var mobile = /Android|iPhone|iPad|iPod/i.test(navigator.userAgent);
    if (mobile && navigator.share) {
      navigator.share({ text: text }).then(
        function () { done(1); },
        function (err) {
          if (err && err.name === 'AbortError') done(3);
          else copy();
        });
    } else {
      copy();
    }
  }
});
