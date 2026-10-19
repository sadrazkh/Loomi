<script setup>
import { ref, reactive, computed, watch, nextTick, onMounted } from 'vue';
import { ArrowUp, Paperclip, LayoutTemplate, X, LoaderCircle, CircleAlert } from 'lucide-vue-next';
import ConfirmDialog from './studio/ConfirmDialog.vue';
import { payLine } from './studio/shared.js';
import { t, tf, n, isOwner, busy, quota, choices, provider, MAX_INPUTS, api, action, uploadFiles, cancelAll, go } from '../core';
// The one box people write in, on the home page (a new chat) and inside a chat (its next message).
// submit receives { prompt, inputs } and does the sending; the box empties only once it went through.
// A draft lives per place (draftKey), so leaving for another page and coming back finds what was being written still there.
const props = defineProps({
 mode: { type: String, default: 'home' }, draftKey: { type: String, default: 'home' }, submit: Function, placeholder: String,
 waiting: Boolean, showProvider: Boolean,
});
const box = ref(null), fileInput = ref(null), dragging = ref(false), asking = ref(false);
const draft = computed(() => drafts[props.draftKey] ||= reactive({ prompt: '', attachments: [] }));
const full = computed(() => draft.value.attachments.length >= MAX_INPUTS);
// Making images may be paused (the shell says so); a request sent then waits in the queue, so sending stays open.
const quotaDone = computed(() => quota.value?.dailyRemaining === 0);
const can = computed(() => !busy.value && !props.waiting && !quotaDone.value && !!draft.value.prompt.trim());
const pay = computed(() => payLine());
const keys = computed(() => /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent) ? '⌘ + Enter' : 'Ctrl + Enter');

function fit() {
 const el = box.value; if (!el) return;
 el.style.height = 'auto'; el.style.height = `${Math.min(el.scrollHeight, Math.round(window.innerHeight * 0.4))}px`;
}
watch(() => [draft.value.prompt, props.draftKey], () => nextTick(fit));
onMounted(fit);

async function send() {
 if (!can.value) return;
 const d = draft.value, body = { prompt: d.prompt, inputs: d.attachments.map(a => ({ uploadId: a.id })) };
 let sent = false;
 await action(async () => { await props.submit(body); sent = true; });
 // The photos now belong to the message, so they are let go of here without being deleted.
 if (sent) { d.prompt = ''; d.attachments = []; }
}
// Ctrl+Enter (⌘+Enter) sends; a plain Enter is a new line, since a description often runs to several.
function key(e) { if (e.ctrlKey || e.metaKey) { e.preventDefault(); send(); } }

async function add(list) {
 const d = draft.value, files = [...list].filter(f => f.type.startsWith('image/')).slice(0, MAX_INPUTS - d.attachments.length);
 if (!files.length) return;
 await action(async () => { for (const u of await uploadFiles(files)) d.attachments.push({ id: u.id, url: `/api/uploads/${u.id}` }); });
}
function attach(e) { const files = [...e.target.files]; e.target.value = ''; add(files); }
function drop(e) { dragging.value = false; add(e.dataTransfer?.files || []); }
function paste(e) { const files = [...(e.clipboardData?.files || [])].filter(f => f.type.startsWith('image/')); if (files.length) { e.preventDefault(); add(files); } }
// Best effort: a file nobody sends is swept after a day anyway.
function detach(a) { draft.value.attachments = draft.value.attachments.filter(x => x.id !== a.id); api(`/api/uploads/${a.id}`, 'DELETE').catch(() => {}); }
async function stopAll() { await cancelAll(); asking.value = false; }

defineExpose({
 focus: () => box.value?.focus(),
 /** Puts a suggestion in the box, for the starter ideas on an empty home page. */
 fill: text => { draft.value.prompt = text; nextTick(() => { box.value?.focus(); fit(); }); },
});
</script>
<script>
// Kept outside the component, so a draft outlives the page it was written on until it is sent or the tab is closed.
const drafts = {};
</script>
<template>
 <form class="composer" :class="[`m-${mode}`, { dragging }]" @submit.prevent="send" @dragover.prevent="dragging = !full" @dragleave="dragging = false" @drop.prevent="drop">
  <div v-if="draft.attachments.length" class="atts">
   <span v-for="a in draft.attachments" :key="a.id" class="att"><img :src="a.url" alt=""><button type="button" class="att-x" :aria-label="t('stRemovePhoto')" :title="t('stRemovePhoto')" @click="detach(a)"><X/></button></span>
   <span class="att-count num">{{ n(draft.attachments.length) }} / {{ n(MAX_INPUTS) }}</span>
  </div>
  <textarea ref="box" v-model="draft.prompt" class="box" dir="auto" :rows="mode==='home' ? 3 : 1" maxlength="12000" :placeholder="placeholder" :aria-label="placeholder" @keydown.enter="key" @input="fit" @paste="paste"></textarea>
  <div class="tools">
   <input ref="fileInput" type="file" accept="image/png,image/jpeg,image/webp" multiple hidden tabindex="-1" @change="attach">
   <button type="button" class="chip attach" :disabled="busy || full" :title="tf('stAttachHint', MAX_INPUTS)" :aria-label="tf('stAttachHint', MAX_INPUTS)" @click="fileInput?.click()"><Paperclip/><span class="lbl">{{ t('stPhoto') }}</span></button>
   <button v-if="mode==='home'" type="button" class="chip" @click="go('templates')"><LayoutTemplate/>{{ t('stTemplate') }}</button>
   <select v-if="showProvider && choices.length > 1" v-model="provider" class="prov" :aria-label="t('stProvider')"><option v-for="p in choices" :key="p" :value="p">{{ p }}</option></select>
   <span class="grow"/>
   <span v-if="waiting" class="note" aria-live="polite"><LoaderCircle class="spin"/>{{ t('stWaitNext') }}</span>
   <span v-else-if="quotaDone" class="note warn" aria-live="polite"><CircleAlert/>{{ t('stQuotaDone') }}</span>
   <span v-else-if="pay" class="note">{{ pay.lead }}<b v-if="pay.name" dir="auto">{{ pay.name }}</b>{{ pay.tail }}<template v-if="quota">{{ t('stSep') }}{{ tf('stQuotaLeft', quota.dailyRemaining) }}</template></span>
   <span v-else-if="quota" class="note">{{ tf('stQuotaLeft', quota.dailyRemaining) }}</span>
   <button v-if="waiting" type="button" class="btn btn-ghost btn-sm cancel" :disabled="busy" @click="asking = true"><X/>{{ t('stCancelAll') }}</button>
   <button class="send" :disabled="!can" :aria-label="t('stSend')" :title="`${t('stSend')} (${keys})`"><ArrowUp/></button>
  </div>
 </form>
 <ConfirmDialog v-if="asking" :title="t('stCancelAllTitle')" :body="t('stCancelAllBody')" :note="isOwner ? t('stCancelAllOwner') : ''" :confirm-label="t('stCancelAllYes')" @confirm="stopAll" @close="asking = false"/>
</template>
<style scoped>
.composer{display:flex;flex-direction:column;gap:12px;width:100%;padding:16px 16px 12px;background:var(--s1);border:1px solid var(--line);border-radius:calc(var(--r) + 6px);box-shadow:var(--shadow);transition:border-color .15s,box-shadow .15s}
.composer:focus-within{border-color:var(--accent-line)}
.composer.dragging{border-color:var(--accent);background:color-mix(in srgb,var(--accent-soft) 40%,var(--s1))}
.m-chat{padding:10px 12px;gap:8px;box-shadow:var(--shadow-sm)}
.box{display:block;width:100%;min-height:72px;padding:0 2px;border:0;background:none;outline:none;resize:none;color:var(--text);font-size:16px;line-height:1.75}
.m-chat .box{min-height:28px;font-size:14.5px}
.box::placeholder{color:var(--faint)}
.atts{display:flex;flex-wrap:wrap;align-items:center;gap:8px}
.att{position:relative;width:64px;height:64px;border-radius:var(--r2);overflow:hidden;background:var(--s3);flex:none}
.m-chat .att{width:54px;height:54px}
.att img{width:100%;height:100%;object-fit:cover;display:block}
.att-x{position:absolute;top:4px;inset-inline-end:4px;width:22px;height:22px;padding:0;border:0;border-radius:50%;display:grid;place-items:center;background:color-mix(in srgb,var(--bg) 78%,transparent);color:var(--text)}
.att-x:hover{background:var(--bad);color:var(--accent-ink)}
.att-x svg.lucide{width:13px;height:13px}
.att-count{font-size:12px;color:var(--faint)}
.tools{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.prov{height:32px;padding:0 12px;border-radius:99px;border:1px solid var(--line);background:var(--s2);color:var(--muted);font-size:13px}
.prov:focus{outline:none;border-color:var(--accent-line)}
.note{display:inline-flex;align-items:center;gap:6px;font-size:12.5px;color:var(--muted);text-align:end;min-width:0}
.note b{color:var(--text);font-weight:600;margin-inline:.2em}
.note svg.lucide{width:15px;height:15px}
.note.warn{color:var(--warn)}
.send{width:38px;height:38px;flex:none;border-radius:50%;border:0;padding:0;display:grid;place-items:center;background:var(--accent);color:var(--accent-ink);transition:background .15s,transform .1s}
.send:hover:not(:disabled){background:color-mix(in srgb,var(--accent) 88%,var(--text))}
.send:active:not(:disabled){transform:scale(.94)}
.send:disabled{background:var(--s3);color:var(--faint)}
.send svg.lucide{width:19px;height:19px;stroke-width:2.2}
@media (max-width:860px){
 .m-home{padding:14px 14px 10px}
 .m-home .box{min-height:64px;font-size:15px}
 .m-home .note{order:10;flex-basis:100%;justify-content:flex-start;text-align:start}
 /* In a chat on a phone the box is one row: photo, words, send, with any line about the wait or the price under it. */
 .m-chat{flex-direction:row;flex-wrap:wrap;align-items:flex-end;gap:6px 8px;padding:7px 8px}
 .m-chat .tools{display:contents}
 .m-chat .atts{order:0;flex-basis:100%}
 .m-chat .attach{order:1;width:38px;height:38px;padding:0;justify-content:center}
 .m-chat .attach .lbl{display:none}
 .m-chat .box{order:2;flex:1;min-width:0;align-self:center;padding:5px 2px}
 .m-chat .send{order:3}
 .m-chat .grow{display:none}
 .m-chat .prov{order:4}
 .m-chat .note{order:5;flex:1 1 auto;justify-content:flex-start;text-align:start;padding-inline:4px}
 .m-chat .cancel{order:6}
}
</style>
