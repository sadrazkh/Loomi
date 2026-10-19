<script setup>
import { ref, computed } from 'vue';
import { KeyRound, MessageSquarePlus, Hourglass, Copy, Check, FileJson, ChevronDown } from 'lucide-vue-next';
import { t, n } from '../core';
// The examples are the very scripts docs/API.md walks through, run against a live server before they were shipped; only the address is swapped in.
import shell from './api-examples/loomi.sh?raw';
import python from './api-examples/loomi.py?raw';
import node from './api-examples/loomi.mjs?raw';
// For everyone signed in: where to send requests, where the token comes from, and one complete example per language that runs as it is copied.
const base = window.location.origin;
const PLACEHOLDER = 'https://loomi.example.com';
// A checkout on Windows can hand the files over with CRLF, and a bash script copied that way fails on the first line.
const fill = source => source.replace(/\r\n/g, '\n').replaceAll(PLACEHOLDER, base);
const LANGUAGES = [
 { key: 'curl', label: 'curl', code: fill(shell), run: 'LOOMI_TOKEN=lm_… bash loomi.sh' },
 { key: 'python', label: 'Python', code: fill(python), run: 'pip install requests && LOOMI_TOKEN=lm_… python loomi.py' },
 { key: 'javascript', label: 'JavaScript', code: fill(node), run: 'LOOMI_TOKEN=lm_… node loomi.mjs' },
];
const STEPS = [
 { icon: KeyRound, title: 'apiGuideStep1', help: 'apiGuideStep1Help', code: 'Authorization: Bearer lm_…' },
 { icon: MessageSquarePlus, title: 'apiGuideStep2', help: 'apiGuideStep2Help', code: 'POST /api/v1/chats' },
 { icon: Hourglass, title: 'apiGuideStep3', help: 'apiGuideStep3Help', code: 'GET /api/v1/messages/{id}/wait' },
];
const ENDPOINTS = [
 ['GET', '/api/v1/me', 'apiEpMe'], ['POST', '/api/v1/chats', 'apiEpStart'], ['GET', '/api/v1/chats', 'apiEpChats'], ['GET', '/api/v1/chats/{id}', 'apiEpChat'],
 ['DELETE', '/api/v1/chats/{id}', 'apiEpDelete'], ['POST', '/api/v1/chats/{id}/messages', 'apiEpSend'], ['GET', '/api/v1/messages/{id}', 'apiEpMessage'],
 ['GET', '/api/v1/messages/{id}/wait', 'apiEpWait'], ['GET', '/api/v1/messages/{id}/events', 'apiEpEvents'], ['GET', '/api/v1/messages/{id}/image', 'apiEpImage'],
 ['POST', '/api/v1/messages/{id}/cancel', 'apiEpCancel'], ['GET', '/api/v1/templates', 'apiEpTemplates'], ['POST', '/api/v1/templates/{id}/use', 'apiEpUse'],
];
// A path wraps only after a slash, never in the middle of a word.
const breakable = text => text.split(/(?<=\/)/);
const TIPS = ['apiGuideTipBusy', 'apiGuideTipAnswered', 'apiGuideTipLimit', 'apiGuideTipImages'];
const tab = ref('curl'), copied = ref(''), pre = ref(null);
const current = computed(() => LANGUAGES.find(l => l.key === tab.value));
let timer = null;
// Without a secure origin there is no clipboard to write to; the text is selected instead, so Ctrl+C finishes the job.
async function copy(key, text, element) {
 try { await navigator.clipboard.writeText(text); copied.value = key; clearTimeout(timer); timer = setTimeout(() => copied.value = '', 2000); }
 catch { if (element) window.getSelection()?.selectAllChildren(element); }
}
</script>

<template>
 <section class="guide" aria-labelledby="ag-title">
  <div>
   <h3 id="ag-title">{{ t('apiGuide') }}</h3>
   <p class="muted-text">{{ t('apiGuideHelp') }}</p>
  </div>

  <div class="base">
   <span class="faint-text">{{ t('apiGuideBase') }}</span>
   <code dir="ltr">{{ base }}/api/v1</code>
   <button class="btn btn-ghost btn-icon btn-sm" :aria-label="t('apiGuideCopy')" :title="t('apiGuideCopy')" @click="copy('base', base + '/api/v1')"><Check v-if="copied==='base'"/><Copy v-else/></button>
  </div>

  <ol class="steps">
   <li v-for="(s, i) in STEPS" :key="s.title">
    <span class="mark"><component :is="s.icon"/><b>{{ n(i + 1) }}</b></span>
    <span class="txt"><strong>{{ t(s.title) }}</strong><small>{{ t(s.help) }}</small><code dir="ltr"><template v-for="(part, j) in breakable(s.code)" :key="j">{{ part }}<wbr></template></code></span>
   </li>
  </ol>

  <div class="example">
   <div class="tabs">
    <div class="seg" role="tablist" :aria-label="t('apiGuideExample')"><button v-for="l in LANGUAGES" :key="l.key" role="tab" :aria-selected="tab===l.key" :aria-pressed="tab===l.key" @click="tab=l.key">{{ l.label }}</button></div>
    <span class="grow"/>
    <button class="btn btn-sm" @click="copy(tab, current.code, pre)"><Check v-if="copied===tab"/><Copy v-else/>{{ t(copied===tab ? 'apiGuideCopied' : 'apiGuideCopy') }}</button>
   </div>
   <p class="field-help">{{ t('apiGuideExampleHelp') }}</p>
   <pre ref="pre" class="code" dir="ltr" role="tabpanel" tabindex="0"><code>{{ current.code }}</code></pre>
   <p class="run"><span class="muted-text">{{ t('apiGuideRun') }}</span> <code dir="ltr">{{ current.run }}</code></p>
  </div>

  <div>
   <h4>{{ t('apiGuideTips') }}</h4>
   <ul class="tips"><li v-for="k in TIPS" :key="k">{{ t(k) }}</li></ul>
  </div>

  <details class="endpoints">
   <summary><ChevronDown/>{{ t('apiGuideEndpoints') }}</summary>
   <ul><li v-for="[method, path, k] in ENDPOINTS" :key="method + path"><code dir="ltr" class="route"><b :class="method.toLowerCase()">{{ method }}</b> {{ path }}</code><span>{{ t(k) }}</span></li></ul>
  </details>

  <footer class="foot">
   <a class="btn btn-sm" href="/api/v1/openapi.json" target="_blank" rel="noopener noreferrer"><FileJson/>{{ t('apiGuideReference') }}</a>
   <small class="faint-text">{{ t('apiGuideFull') }}</small>
  </footer>
 </section>
</template>

<style scoped>
.guide{display:flex;flex-direction:column;gap:16px;min-width:0}
.guide h3{font-size:16px}
.guide h4{font-size:14px;margin-bottom:6px}
.base{display:flex;align-items:center;gap:10px;padding-block:6px;padding-inline:14px 6px;border-radius:var(--r2);border:1px solid var(--line);background:var(--s2);font-size:12.5px}
.base code{flex:1;min-width:0;overflow-wrap:anywhere;color:var(--text);font-size:13px}
.steps{list-style:none;margin:0;padding:0;display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px}
.steps li{display:flex;gap:12px;padding:14px;border-radius:var(--r);background:var(--s2);min-width:0}
.mark{position:relative;flex:none;width:32px;height:32px;display:grid;place-items:center;border-radius:var(--r2);background:var(--accent-soft);color:var(--accent)}
.mark svg{width:16px;height:16px}
.mark b{position:absolute;top:-6px;inset-inline-end:-6px;min-width:17px;height:17px;padding:0 4px;display:grid;place-items:center;border-radius:99px;background:var(--accent);color:var(--accent-ink);font-size:10.5px;line-height:1}
.txt{display:flex;flex-direction:column;gap:3px;min-width:0}
.txt strong{font-size:13.5px}
.txt small{color:var(--muted);font-size:12.5px;line-height:1.6}
.txt code{align-self:flex-start;margin-top:4px;padding:2px 7px;border-radius:6px;background:var(--s3);color:var(--text);font-size:11.5px;max-width:100%}
.example{display:flex;flex-direction:column;gap:8px;min-width:0}
.tabs{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.code{margin:0;max-height:360px;overflow:auto;padding:14px;border-radius:var(--r2);background:var(--bg);border:1px solid var(--line);color:var(--text);font-family:ui-monospace,'Cascadia Mono',Consolas,monospace;font-size:12px;line-height:1.6;tab-size:2;text-align:left}
.run{display:flex;flex-wrap:wrap;align-items:baseline;gap:6px;font-size:12.5px}
.run code{color:var(--accent);overflow-wrap:anywhere}
.tips{margin:0;padding-inline-start:18px;display:grid;gap:6px;color:var(--muted);font-size:13px}
.endpoints{border:1px solid var(--line);border-radius:var(--r2)}
.endpoints summary{display:flex;align-items:center;gap:8px;cursor:pointer;padding:10px 14px;font-size:13px;color:var(--muted);list-style:none}
.endpoints summary::-webkit-details-marker{display:none}
.endpoints summary svg{width:16px;height:16px;transition:transform .15s}
.endpoints[open] summary svg{transform:rotate(180deg)}
.endpoints ul{list-style:none;margin:0;padding:0 14px 12px;display:grid;gap:8px}
.endpoints li{display:grid;grid-template-columns:minmax(0,300px) 1fr;gap:12px;align-items:baseline;font-size:12.5px;color:var(--muted)}
.route{font-size:11.5px;color:var(--text);overflow-wrap:anywhere;direction:ltr;text-align:left}
.route b{display:inline-block;min-width:54px;font-weight:600}
.route .get{color:var(--ok)}
.route .post{color:var(--accent)}
.route .delete{color:var(--bad)}
.foot{display:flex;flex-wrap:wrap;align-items:center;gap:10px}
@media (max-width:760px){.steps{grid-template-columns:1fr}.endpoints li{grid-template-columns:1fr;gap:2px}}
</style>
