<script setup>
import { ref, computed } from 'vue';
import { SquareTerminal, KeyRound, MessageSquarePlus, Hourglass, Copy, Check, FileJson } from 'lucide-vue-next';
// The examples are the very scripts docs/API.md walks through, run against a live server before they were shipped; only the address is swapped in.
import shell from './api-examples/loomi.sh?raw';
import python from './api-examples/loomi.py?raw';
import node from './api-examples/loomi.mjs?raw';
// For everyone signed in: where to send requests, where the token comes from, and one complete example per language that runs as it is copied.
defineProps({ t: Function, locale: String });
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
 <section class="settings-panel api-guide">
  <header class="ag-head"><span class="ag-mark"><SquareTerminal/></span><span><h2>{{ t('apiGuide') }}</h2><p>{{ t('apiGuideHelp') }}</p></span></header>

  <div class="ag-base">
   <span>{{ t('apiGuideBase') }}</span>
   <code dir="ltr">{{ base }}/api/v1</code>
   <button class="icon-button" :aria-label="t('apiGuideCopy')" @click="copy('base', base + '/api/v1')"><Check v-if="copied==='base'"/><Copy v-else/></button>
  </div>

  <ol class="ag-steps">
   <li v-for="(s, i) in STEPS" :key="s.title">
    <span class="ag-step-mark"><component :is="s.icon"/><b>{{ (i + 1).toLocaleString(locale) }}</b></span>
    <span><strong>{{ t(s.title) }}</strong><small>{{ t(s.help) }}</small><code dir="ltr"><template v-for="(part, j) in breakable(s.code)" :key="j">{{ part }}<wbr></template></code></span>
   </li>
  </ol>

  <div class="ag-example">
   <div class="ag-tabs" role="tablist" :aria-label="t('apiGuideExample')">
    <button v-for="l in LANGUAGES" :key="l.key" role="tab" :aria-selected="tab===l.key" :class="{active:tab===l.key}" @click="tab=l.key">{{ l.label }}</button>
    <span class="grow"/>
    <button class="ag-copy" @click="copy(tab, current.code, pre)"><Check v-if="copied===tab"/><Copy v-else/>{{ t(copied===tab ? 'apiGuideCopied' : 'apiGuideCopy') }}</button>
   </div>
   <p class="ag-caption">{{ t('apiGuideExampleHelp') }}</p>
   <pre ref="pre" class="ag-code" dir="ltr" role="tabpanel" tabindex="0"><code>{{ current.code }}</code></pre>
   <p class="ag-run"><span>{{ t('apiGuideRun') }}</span> <code dir="ltr">{{ current.run }}</code></p>
  </div>

  <h3 class="ag-subhead">{{ t('apiGuideTips') }}</h3>
  <ul class="ag-tips"><li v-for="k in TIPS" :key="k">{{ t(k) }}</li></ul>

  <details class="ag-endpoints">
   <summary>{{ t('apiGuideEndpoints') }}</summary>
   <ul><li v-for="[method, path, k] in ENDPOINTS" :key="method + path"><code dir="ltr" class="ag-route"><b :class="method.toLowerCase()">{{ method }}</b> {{ path }}</code><span>{{ t(k) }}</span></li></ul>
  </details>

  <footer class="accounts-footer">
   <a class="button" href="/api/v1/openapi.json" target="_blank" rel="noopener noreferrer"><FileJson/>{{ t('apiGuideReference') }}</a>
   <span class="grow"/><small>{{ t('apiGuideFull') }}</small>
  </footer>
 </section>
</template>

<style scoped>
.ag-head{display:flex;gap:14px;align-items:flex-start}
.ag-head h2{margin:0}
.ag-head p{margin:4px 0 0}
.ag-mark{width:40px;height:40px;flex-shrink:0;display:grid;place-items:center;background:var(--accent-dim);border:1px solid var(--accent-line);color:var(--accent)}
.ag-mark svg{width:20px;height:20px}
.ag-base{display:flex;align-items:center;gap:10px;margin-top:18px;padding:10px 12px 10px 14px;border:1px solid var(--line);background:var(--s2);font-size:12px;color:var(--muted)}
.ag-base code{flex:1;min-width:0;overflow-wrap:anywhere;color:var(--text);font-family:ui-monospace,Consolas,monospace;font-size:13px}
.ag-steps{list-style:none;margin:18px 0 0;padding:0;display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:1px;background:var(--line);border:1px solid var(--line)}
.ag-steps li{display:flex;gap:10px;padding:14px;background:var(--s1)}
.ag-steps strong{display:block;font-size:13px}
.ag-steps small{display:block;margin-top:3px;color:var(--muted);font-size:12px;line-height:1.5}
.ag-steps code{display:inline-block;margin-top:8px;padding:2px 6px;background:var(--s2);color:var(--accent);font-family:ui-monospace,Consolas,monospace;font-size:11px}
.ag-step-mark{position:relative;flex-shrink:0;width:30px;height:30px;display:grid;place-items:center;border:1px solid var(--line2);color:var(--accent)}
.ag-step-mark svg{width:15px;height:15px}
.ag-step-mark b{position:absolute;top:-7px;inset-inline-end:-7px;min-width:16px;height:16px;padding:0 3px;display:grid;place-items:center;background:var(--accent);color:var(--bg);font-size:10px;line-height:1}
.ag-example{margin-top:22px;border:1px solid var(--line)}
.ag-tabs{display:flex;align-items:stretch;border-bottom:1px solid var(--line);background:var(--s2)}
.ag-tabs [role=tab]{border:0;border-inline-end:1px solid var(--line);background:transparent;padding:9px 16px;font-size:12px;color:var(--muted)}
.ag-tabs [role=tab].active{background:var(--s1);color:var(--accent);box-shadow:inset 0 -2px 0 var(--accent)}
.ag-copy{border:0;border-inline-start:1px solid var(--line);background:transparent;font-size:12px;padding:9px 14px}
.ag-caption{margin:0;padding:10px 14px 0;font-size:12px}
.ag-code{margin:10px 0 0;max-height:360px;overflow:auto;padding:14px;background:var(--bg);border-block:1px solid var(--line);color:var(--text);font-family:ui-monospace,Consolas,monospace;font-size:12px;line-height:1.55;tab-size:2;text-align:left}
.ag-run{display:flex;flex-wrap:wrap;align-items:baseline;gap:6px;margin:0;padding:10px 14px;font-size:12px}
.ag-run code{color:var(--accent);font-family:ui-monospace,Consolas,monospace;font-size:12px;overflow-wrap:anywhere}
.ag-subhead{margin:22px 0 8px;font-size:14px}
.ag-tips{margin:0;padding-inline-start:18px;display:grid;gap:6px;color:var(--muted);font-size:12px}
.ag-endpoints{margin-top:18px;border:1px solid var(--line)}
.ag-endpoints summary{cursor:pointer;padding:10px 14px;font-size:12px;color:var(--muted)}
.ag-endpoints ul{list-style:none;margin:0;padding:4px 14px 12px;display:grid;gap:6px}
.ag-endpoints li{display:grid;grid-template-columns:minmax(0,300px) 1fr;gap:12px;align-items:baseline;font-size:12px;color:var(--muted)}
.ag-route{font-family:ui-monospace,Consolas,monospace;font-size:11.5px;color:var(--text);overflow-wrap:anywhere}
.ag-route b{display:inline-block;min-width:52px;font-weight:600}
.ag-route .get{color:var(--ok)}
.ag-route .post{color:var(--accent)}
.ag-route .delete{color:var(--bad)}
@media (max-width:760px){.ag-steps{grid-template-columns:1fr}.ag-endpoints li{grid-template-columns:1fr;gap:2px}}
</style>
