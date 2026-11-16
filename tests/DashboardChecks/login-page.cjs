// Dependency-free JavaScript/DOM-contract checks, without a real browser or QQ.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

async function run() {
  const html = fs.readFileSync(path.join(__dirname, '../../src/Dashboard/index.html'), 'utf8');
  const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
  const elements = Object.fromEntries([...html.matchAll(/\bid="([^"]+)"/g)].map(([, id]) => [id, {
    value: '', hidden: false, disabled: false, textContent: '',
    reportValidity() { return !this.invalid; },
    removeAttribute(name) { delete this[name]; }
  }]));
  const requests = [];
  const state = { protocol: 'AndroidPad', qrSupported: false, status: 'sms', passwordlessSmsSupported: true, verificationId: 'first',
    verificationUrl: 'https://example.com/verify', verificationPhone: '+86-138****0000' };
  const context = vm.createContext({
    URL, console, setTimeout() {}, navigator: {},
    document: {
      getElementById(id) { assert.ok(elements[id], 'Missing element: ' + id); return elements[id]; },
      querySelectorAll() { return [elements.quick, elements.scan, elements['copy-url']]; },
      createRange() { return { selectNodeContents() {} }; }
    },
    window: {
      addEventListener() {}, getSelection() { return { removeAllRanges() {}, addRange() {} }; },
      ShiroBotDashboard: { ready: Promise.resolve(), async call(action, args) {
        requests.push({ action, args });
        return action === 'status' ? state : { protocol: 'AndroidPad', qrSupported: false, status: 'logging_in' };
      } }
    }
  });
  vm.runInContext(script, context);
  const flush = () => new Promise(resolve => setImmediate(resolve));
  await flush();
  // Clicking a type=button must work even if the host sandbox never emits a
  // native form submit event. No real credentials or network calls are used.
  for (const id of ['password-submit', 'sms-login-submit', 'captcha-submit', 'sms-submit'])
    assert.match(html, new RegExp(`id="${id}" type="button"`));
  elements.uin.value = '199999998';
  elements.password.value = 'synthetic-password';
  elements.login.invalid = true;
  elements['password-submit'].onclick();
  await flush();
  assert.equal(requests.filter(x => x.action === 'password-login').length, 0);
  assert.equal(elements.password.value, 'synthetic-password');
  elements.login.invalid = false;
  elements['password-submit'].onclick();
  await flush();
  assert.equal(requests.filter(x => x.action === 'password-login').length, 1);
  assert.equal(elements.password.value, '');
  elements.password.value = 'synthetic-password';
  let prevented = false;
  elements.login.onkeydown({key:'Enter',target:{tagName:'INPUT'},preventDefault(){prevented=true;}});
  await flush();
  assert.equal(prevented, true);
  assert.equal(requests.filter(x => x.action === 'password-login').length, 2);
  elements['sms-uin'].value = '199999998';
  elements['sms-login-submit'].onclick();
  await flush();
  assert.equal(requests.filter(x => x.action === 'sms-login').length, 1);
  vm.runInContext('render(testInitialState)', Object.assign(context,{testInitialState:state}));
  assert.equal(elements.scan.disabled, true);
  assert.equal(elements.sms.hidden, false);
  assert.equal(elements['sms-login'].hidden, false);
  assert.equal(elements.phone.textContent, state.verificationPhone);
  assert.equal(elements['open-url'].href, state.verificationUrl);
  assert.equal(elements['open-url'].hidden, false);
  elements.code.value = '123456';
  context.testState = state;
  vm.runInContext('render(testState)', context);
  assert.equal(elements.code.value, '123456', 'Polling the same challenge cleared input');
  state.verificationId = 'second';
  vm.runInContext('render(testState)', context);
  assert.equal(elements.code.value, '', 'New challenge retained a previous verification code');
  state.status = 'captcha';
  vm.runInContext('render(testState)', context);
  assert.equal(elements.captcha.hidden, false);
  assert.equal(elements.randstr.required, false);
  assert.equal(elements.randstr.hidden, true);
  assert.equal(elements['randstr-label'].hidden, true);
  assert.equal(elements['captcha-result'].hidden, true);
  elements.ticket.value = '{"ticket":"synthetic-single-ticket"}';
  elements.ticket.oninput();
  assert.equal(elements.ticket.value, 'synthetic-single-ticket');
  elements.ticket.value = 'synthetic-plain-ticket';
  elements.ticket.oninput();
  assert.equal(elements.ticket.value, 'synthetic-plain-ticket');
  context.pcState = {...state,protocol:'MacOs'};
  vm.runInContext('render(pcState)', context);
  assert.equal(elements.randstr.hidden, false);
  assert.equal(elements.randstr.required, true);
  assert.equal(elements['captcha-result'].hidden, false);
  vm.runInContext('render(testState)', context);
  elements.ticket.value = 'terror_1006_synthetic';
  elements['captcha-submit'].onclick();
  await flush();
  assert.equal(requests.filter(x => x.action === 'captcha').length, 0, 'Invalid helper result was submitted');
  assert.ok(elements.message.textContent.includes('加载失败'));
  elements['captcha-result'].value = '{"ret":0,"ticket":"synthetic-ticket","randstr":"synthetic-rand"}';
  elements['captcha-result'].oninput();
  assert.equal(elements.ticket.value, 'synthetic-ticket');
  assert.equal(elements.randstr.value, 'synthetic-rand');
  elements['captcha-submit'].onclick();
  await flush();
  const submission = requests.find(x => x.action === 'captcha');
  assert.equal(submission.args.verificationId, 'second');
  assert.equal(submission.args.ticket, 'synthetic-ticket');
  assert.equal(elements.ticket.value, '');
  assert.equal(elements.scan.disabled, true, 'Completion re-enabled unsupported Android QR');
  state.verificationId = 'third';
  state.verificationUrl = 'javascript:alert(1)';
  vm.runInContext('render(testState)', context);
  assert.equal(elements['open-url'].hidden, true);
  assert.equal(elements['open-url'].href, undefined);
  state.verificationUrl = 'https://example.com/verify';
  vm.runInContext('render(testState)', context);
  await elements['copy-url'].onclick();
  assert.ok(elements.message.textContent.includes('手动复制'));
  console.log('Login page JavaScript/DOM contracts passed: sandbox-compatible click/Enter dispatch and validation, Android QR capability, SMS/links, challenge lifecycle, JSON paste, safe href and copy fallback. No browser rendering or QQ login performed.');
}
run().catch(error => { console.error(error); process.exitCode = 1; });
