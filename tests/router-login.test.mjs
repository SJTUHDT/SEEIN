import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const source = readFileSync(new URL('../src/Seein.Tray/Services/RouterLoginScript.cs', import.meta.url), 'utf8').split('"""')[1];
function fixture() {
  const messages = [], listeners = {};
  let observer, clicks = 0;
  class Input {
    get value() { return this.text ?? ''; }
    set value(text) { this.text = text; }
    dispatchEvent() {}
  }
  const input = new Input();
  const button = { disabled: false, click() { clicks++; listeners.click({target: {closest: () => button}}); } };
  class Request {
    open() {}
    addEventListener(_, callback) { this.loaded = callback; }
    finish(url, result, method = 'POST') {
      this.open(method, url); this.responseText = JSON.stringify(result); this.loaded();
    }
  }
  const document = {
    querySelector: selector => selector.includes('input') ? input : button,
    addEventListener: (type, callback) => { listeners[type] = callback; }
  };
  const window = { chrome: {webview: {postMessage: value => messages.push(value)}} }; window.top = window;
  runInNewContext(source, {
    window, document, XMLHttpRequest: Request, HTMLInputElement: Input, chrome: window.chrome,
    MutationObserver: class { constructor(callback) { observer = callback; } observe() {} },
    URL, location: {href: 'http://192.168.5.1/#/', origin: 'http://192.168.5.1'}, Event: class {}
  });
  return {window, input, button, messages, observer, Request, clicks: () => clicks};
}

test('login form is announced once; failed passwords are never persisted', () => {
  const f = fixture(); f.observer(); f.observer();
  assert.equal(f.messages.filter(m => m.type === 'router-login-ready').length, 1);
  f.input.value = 'test-wrong'; f.button.click();
  new f.Request().finish('/?_type=loginData&_tag=login_entry', {login_need_refresh: false});
  assert.equal(f.messages.filter(m => m.type === 'router-login-success').length, 0);
});

test('successful login persists the entered password only after the device confirms', () => {
  const f = fixture(); f.input.value = 'test-valid'; f.button.click();
  new f.Request().finish('/?_type=loginData&_tag=login_entry', {login_need_refresh: true}, 'GET');
  assert.equal(f.messages.filter(m => m.type === 'router-login-success').length, 0);
  new f.Request().finish('/?_type=loginData&_tag=login_entry', {login_need_refresh: true});
  assert.equal(f.messages.at(-1).type, 'router-login-success');
  assert.equal(f.messages.at(-1).password, 'test-valid');
});

test('saved password reuses the original login button and does not overwrite manual typing', () => {
  const f = fixture(); f.window.__seeinRouterLogin('test-saved');
  assert.equal(f.input.value, 'test-saved'); assert.equal(f.clicks(), 1);
  f.input.value = 'manual'; f.window.__seeinRouterLogin('other');
  assert.equal(f.input.value, 'manual'); assert.equal(f.clicks(), 1);
});

test('unrelated and foreign responses cannot cause password persistence', () => {
  const f = fixture(); f.input.value = 'test-secret'; f.button.click();
  new f.Request().finish('http://192.168.5.2/?_tag=login_entry', {login_need_refresh: true});
  new f.Request().finish('/?_tag=other', {login_need_refresh: true});
  assert.equal(f.messages.filter(m => m.type === 'router-login-success').length, 0);
});
