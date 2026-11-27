namespace Seein.Tray;

// Retain the router's own challenge/hash flow; persist only a successful login.
internal static class RouterLoginScript
{
    public const string Source = """
    (() => {
      if (window !== window.top || !window.chrome?.webview) return;
      let pending = '', announced = false;
      const field = () => document.querySelector('#login .comboPwdInput input');
      const button = () => document.querySelector('#login button.loginbtn');
      const remember = () => { const input = field(); if (input) pending = input.value; };
      document.addEventListener('click', e => { if (e.target.closest?.('#login button.loginbtn')) remember(); }, true);
      document.addEventListener('keydown', e => { if (e.key === 'Enter' && e.target === field()) remember(); }, true);
      const open = XMLHttpRequest.prototype.open;
      XMLHttpRequest.prototype.open = function(method, url, ...rest) {
        this.addEventListener('load', () => {
          try {
            const address = new URL(url, location.href);
            if (address.origin !== location.origin || address.searchParams.get('_tag') !== 'login_entry' || String(method).toUpperCase() !== 'POST' || !pending) return;
            const result = typeof this.response === 'object' && this.response ? this.response : JSON.parse(this.responseText);
            if (result.login_need_refresh === true || result.login_need_refresh === 'true' || result.login_need_refresh === '1' || result.login_need_refresh === 1) {
              chrome.webview.postMessage({type:'router-login-success',password:pending}); pending = '';
            } else if (String(method).toUpperCase() === 'POST') pending = '';
          } catch {}
        });
        return open.call(this, method, url, ...rest);
      };
      window.__seeinRouterLogin = password => {
        const input = field(), login = button();
        if (!input || !login || login.disabled || input.value) return;
        Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, password);
        input.dispatchEvent(new Event('input', {bubbles:true}));
        login.click();
      };
      const observe = () => {
        if (!announced && field() && button()) { announced = true; chrome.webview.postMessage({type:'router-login-ready'}); }
      };
      new MutationObserver(observe).observe(document, {childList:true,subtree:true});
      observe();
    })();
    """;
}
