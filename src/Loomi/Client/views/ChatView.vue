<script setup>
import { ref, computed, watch, nextTick, onMounted, onBeforeUnmount } from 'vue';
import { ArrowRight, Plus, Ellipsis, ExternalLink, Trash2, MessagesSquare, List, GitBranch, LayoutTemplate, AlertTriangle, MessageSquarePlus, LoaderCircle, Pencil, GitBranchPlus } from 'lucide-vue-next';
import Composer from '../components/Composer.vue';
import ChatTurn from '../components/ChatTurn.vue';
import GenerationCard from '../components/GenerationCard.vue';
import Modal from '../components/Modal.vue';
import ImagePreview from '../components/studio/ImagePreview.vue';
import ConfirmDialog from '../components/studio/ConfirmDialog.vue';
import { narrow } from '../components/studio/shared.js';
import { t, n, busy, isOwner, ready, current, generations, view, waiting, latest, started, latestPicture, connected, provider, quota, api, action, refresh, reloadCurrent, startChat, deleteProject, go } from '../core';
// One chat: its messages and answers, the box for the next message, and what can be done to the chat itself.
// On a phone the shell steps aside here, so this page brings its own header (with the way back) and keeps the box at the bottom.

// The timeline and the branch tree are kept from the first version as a second way to look over a long chat; the choice is remembered.
const LAYOUTS = [['chat', MessagesSquare, 'stLayoutChat'], ['timeline', List, 'stLayoutTimeline'], ['tree', GitBranch, 'stLayoutTree']];
const stored = () => { try { return localStorage.getItem('loomi.layout'); } catch { return null; } };
const layout = ref(LAYOUTS.some(([k]) => k === stored()) ? stored() : 'chat');
watch(layout, v => { try { localStorage.setItem('loomi.layout', v); } catch { /* private window: it lasts this visit */ } });

const composer = ref(null), menu = ref(false), menuRoot = ref(null);
const op = ref(null), opPrompt = ref(''), shown = ref(null), asking = ref(false);
const templateTitle = computed(() => generations.value[0]?.templateTitle || '');
const index = computed(() => new Map(generations.value.map((g, i) => [g.id, i + 1])));
const ordered = computed(() => {
 if (layout.value !== 'tree') return generations.value.map(g => ({ g, depth: 0 }));
 const result = [], seen = new Set();
 const visit = (parent, depth) => { for (const g of generations.value.filter(x => (x.parentGenerationId ?? null) === parent)) { if (seen.has(g.id)) continue; seen.add(g.id); result.push({ g, depth }); visit(g.id, depth + 1); } };
 visit(null, 0);
 // A parent outside this chat (a branch's source) leaves its children unvisited; they still belong at the top.
 for (const g of generations.value) if (!seen.has(g.id)) { seen.add(g.id); result.push({ g, depth: 0 }); }
 return result;
});
// Why the queue is not moving. The server's reason tells the owner what to fix; a member is only told their request is safe in the queue,
// and only when the shell's own banner (shown while nothing is connected) is not already saying so.
const stalled = computed(() => { const code = connected.value.stalled; if (!code) return ''; if (isOwner.value) return t(code); return ready.value ? t('stStalledMember') : ''; });
const placeholder = computed(() => t(!started.value ? 'stChatFirst' : latest.value?.status === 'Answered' ? 'stChatAnswer' : narrow.value ? 'stChatContinueShort' : 'stChatContinue'));

// Inside a chat there is only the next message: the server decides whether it continues the conversation, reopens it from the latest picture or starts it.
async function submit({ prompt, inputs }) {
 const id = current.value.id;
 // Refused as busy, the chat has work this page has not seen yet, from another tab or device; showing it is what explains the wait.
 const g = await api(`/api/projects/${id}/messages`, 'POST', { prompt, provider: provider.value, inputs })
  .catch(async e => { if (e.message === 'ProjectBusy') await reloadCurrent().catch(() => {}); throw e; });
 if (current.value?.id === id && !generations.value.some(x => x.id === g.id)) generations.value.push(g);
 await refresh();
}

// Editing the chat's latest picture is just the next message, so it only points at the box; an older picture has its own call, which sends it along.
function operation(kind, g) {
 if (kind === 'edit' && g.id === latestPicture.value?.id) { composer.value?.focus(); return; }
 op.value = { kind, g }; opPrompt.value = '';
}
const opening = (text, count) => { const words = text.trim().split(/\s+/).join(' '); return words.length <= count ? words : words.slice(0, count).trimEnd() + '…'; };
const opBlocked = computed(() => busy.value || !opPrompt.value.trim() || quota.value?.dailyRemaining === 0 || (op.value?.kind === 'edit' && waiting.value));
async function submitOp() {
 if (opBlocked.value) return;
 const { kind, g } = op.value, prompt = opPrompt.value;
 await action(async () => {
  // A branch is a chat of its own, named after the chat it left and what it tries there, so several branches of one chat stay apart in the list;
  // the source is kept short so the words that tell siblings apart come early.
  if (kind === 'branch') { await startChat({ title: `${opening(current.value?.title || '', 32)} ↳ ${opening(prompt, 48)}`.trim(), prompt, inputs: [{ generationId: g.id }] }); op.value = null; return; }
  const next = await api(`/api/generations/${g.id}/edit`, 'POST', { prompt });
  if (!generations.value.some(x => x.id === next.id)) generations.value.push(next);
  op.value = null; await refresh();
 });
}
const opKey = e => { if (e.ctrlKey || e.metaKey) { e.preventDefault(); submitOp(); } };
async function remove() { await deleteProject(); asking.value = false; }

// Polling swaps every generation object every five seconds, so only the newest turn's own state may move the page;
// a status tick on it follows a reader already at the bottom, never one reading back. A new turn always comes into view.
const root = document.documentElement;
const atBottom = () => window.innerHeight + window.scrollY >= root.scrollHeight - 240;
const newest = computed(() => { const g = generations.value.at(-1); return view.value === 'chat' && layout.value === 'chat' && g ? [g.id, g.status, g.imageUrl, g.replyText?.length].join('|') : ''; });
watch(newest, async (next, prev = '') => {
 if (!next) return;
 const added = next.split('|')[0] !== prev.split('|')[0];
 if (!added && !atBottom()) return;
 await nextTick(); window.scrollTo({ top: root.scrollHeight, behavior: prev ? 'smooth' : 'auto' });
}, { immediate: true });
// A picture's height is known only once it arrives, and the font's once it has loaded; a reader who was at the bottom stays there as they land.
let pinned = true;
const onScroll = () => { pinned = atBottom(); };
const col = ref(null);
const follow = new ResizeObserver(() => { if (pinned && layout.value === 'chat') window.scrollTo({ top: root.scrollHeight }); });
watch(col, (el, old) => { if (old) follow.unobserve(old); if (el) follow.observe(el); });
function closeMenu(e) { if (menu.value && !menuRoot.value?.contains(e.target)) menu.value = false; }
function escape(e) { if (e.key === 'Escape') menu.value = false; }
onMounted(() => { window.addEventListener('scroll', onScroll, { passive: true }); document.addEventListener('mousedown', closeMenu); document.addEventListener('keydown', escape); });
onBeforeUnmount(() => { follow.disconnect(); window.removeEventListener('scroll', onScroll); document.removeEventListener('mousedown', closeMenu); document.removeEventListener('keydown', escape); });
// Another chat opened from the side menu: whatever was open belonged to the last one.
watch(() => current.value?.id, () => { op.value = null; shown.value = null; menu.value = false; asking.value = false; pinned = true; });
</script>
<template>
 <div v-if="current" class="conv">
  <header class="top">
   <button type="button" class="btn btn-ghost btn-icon back" :aria-label="t('shBack')" @click="go('chats')"><ArrowRight class="dir"/></button>
   <div class="title"><h1 class="truncate" dir="auto">{{ current.title }}</h1><span v-if="templateTitle" class="pill"><LayoutTemplate/><bdi class="truncate">{{ templateTitle }}</bdi></span></div>
   <span class="grow"/>
   <button type="button" class="btn btn-sm new" @click="go('home')"><Plus/>{{ t('stNewChat') }}</button>
   <div ref="menuRoot" class="more">
    <button type="button" class="btn btn-ghost btn-icon" :aria-label="t('stChatMenu')" aria-haspopup="menu" :aria-expanded="menu" @click="menu = !menu"><Ellipsis/></button>
    <div v-if="menu" class="menu" role="menu">
     <button type="button" role="menuitem" class="phone" @click="menu = false; go('home')"><Plus/>{{ t('stNewChat') }}</button>
     <a v-if="current.conversationUrl" role="menuitem" :href="current.conversationUrl" target="_blank" rel="noopener noreferrer" @click="menu = false"><ExternalLink/>{{ t('stOpenInChatGPT') }}</a>
     <div class="group" role="group" :aria-label="t('stLayout')">
      <span class="label">{{ t('stLayout') }}</span>
      <div class="seg"><button v-for="[key, icon, label] in LAYOUTS" :key="key" type="button" :aria-pressed="layout === key" @click="layout = key; menu = false"><component :is="icon"/>{{ t(label) }}</button></div>
     </div>
     <button type="button" role="menuitem" class="danger" @click="menu = false; asking = true"><Trash2/>{{ t('stDeleteChat') }}</button>
    </div>
   </div>
  </header>

  <main class="thread">
   <div ref="col" class="col" :class="`as-${layout}`">
    <div v-if="!generations.length" class="blank"><MessageSquarePlus/><h2>{{ t('stEmptyChat') }}</h2><p>{{ t('stEmptyChatBody') }}</p></div>
    <template v-else-if="layout === 'chat'">
     <ChatTurn v-for="g in generations" :key="g.id" :generation="g" :latest="g.id === latestPicture?.id" @edit="operation('edit', $event)" @branch="operation('branch', $event)" @preview="shown = $event"/>
    </template>
    <template v-else>
     <div v-for="item in ordered" :key="item.g.id" class="node" :style="{ '--depth': Math.min(item.depth, 5) }">
      <span v-if="layout === 'tree' && item.g.parentGenerationId && index.get(item.g.parentGenerationId)" class="lineage"><GitBranch/>{{ t('stFrom') }} #{{ n(index.get(item.g.parentGenerationId)) }}</span>
      <GenerationCard :generation="item.g" :index="index.get(item.g.id)" :latest="item.g.id === latestPicture?.id" @edit="operation('edit', $event)" @branch="operation('branch', $event)" @preview="shown = $event"/>
     </div>
    </template>
   </div>
  </main>

  <div class="dock">
   <p v-if="stalled" class="notice notice-warn" role="status"><AlertTriangle/><span>{{ stalled }}</span></p>
   <Composer ref="composer" mode="chat" :draft-key="`chat:${current.id}`" :submit="submit" :waiting="waiting" :show-provider="!started" :placeholder="placeholder"/>
  </div>

  <Teleport v-if="op" to="body"><Modal :title="t(op.kind === 'edit' ? 'stEditOlderTitle' : 'stBranchTitle')" :close-label="t('stClose')" @close="op = null">
   <form class="stack" @submit.prevent="submitOp">
    <div class="source"><img :src="op.g.imageUrl" alt=""><p dir="auto">{{ op.g.prompt || op.g.templateTitle }}</p></div>
    <p class="help">{{ t(op.kind === 'edit' ? 'stEditOlderHelp' : 'stBranchHelp') }}</p>
    <div class="field"><label for="op-prompt">{{ t('stChangeLabel') }}</label><textarea id="op-prompt" v-model="opPrompt" class="input" rows="4" maxlength="12000" required dir="auto" :placeholder="t('stChangePlaceholder')" @keydown.enter="opKey"></textarea></div>
    <p v-if="op.kind === 'edit' && waiting" class="notice"><LoaderCircle class="spin"/><span>{{ t('stWaitNext') }}</span></p>
    <div class="dlg-actions">
     <button type="button" class="btn" @click="op = null">{{ t('stCancel') }}</button>
     <button class="btn btn-primary" :disabled="opBlocked"><Pencil v-if="op.kind === 'edit'"/><GitBranchPlus v-else/>{{ t(op.kind === 'edit' ? 'stEditSend' : 'stBranchSend') }}</button>
    </div>
   </form>
  </Modal></Teleport>
  <ImagePreview v-if="shown" :generation="shown" @close="shown = null"/>
  <ConfirmDialog v-if="asking" :title="t('stDeleteChat')" :body="t('stDeleteChatBody')" :confirm-label="t('stDeleteChatYes')" @confirm="remove" @close="asking = false"/>
 </div>
</template>
<style scoped>
.conv{display:flex;flex-direction:column;min-height:100vh;min-height:100dvh}
.top{position:sticky;top:0;z-index:14;display:flex;align-items:center;gap:10px;min-height:58px;padding:0 24px;background:color-mix(in srgb,var(--bg) 88%,transparent);backdrop-filter:blur(10px);-webkit-backdrop-filter:blur(10px);border-bottom:1px solid var(--line)}
.back{display:none}
:root[dir=ltr] .dir{transform:scaleX(-1)}
.title{display:flex;align-items:center;gap:10px;min-width:0}
.title h1{font-size:16px;font-weight:600;line-height:1.5}
.title .pill{max-width:220px;flex:none}
.more{position:relative}
.menu{position:absolute;top:calc(100% + 6px);inset-inline-end:0;z-index:20;width:272px;display:flex;flex-direction:column;padding:6px;border-radius:var(--r);background:var(--s1);border:1px solid var(--line2);box-shadow:var(--shadow)}
.menu>button,.menu>a{display:flex;align-items:center;gap:10px;height:40px;padding:0 10px;border:0;border-radius:var(--r2);background:transparent;color:var(--text);font-size:13.5px;text-align:start;text-decoration:none}
.menu>button:hover,.menu>a:hover{background:var(--s2)}
.menu .danger{color:var(--bad)}
.menu .phone{display:none}
.group{display:flex;flex-direction:column;gap:6px;padding:6px 10px 12px;margin-bottom:4px;border-bottom:1px solid var(--line)}
.label{font-size:12px;color:var(--faint)}
.group .seg{display:flex}
.group .seg>button{flex:1;justify-content:center;padding:0 6px}
.thread{flex:1;display:flex;flex-direction:column;align-items:center;justify-content:flex-end;padding:28px 24px 12px}
.col{width:min(720px,100%);display:flex;flex-direction:column;gap:26px}
/* The cards read from the top down, so they start under the header rather than sinking to the box like a chat. */
.col.as-timeline,.col.as-tree{margin-bottom:auto}
.col.as-timeline{width:min(1080px,100%);display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:18px;align-items:start}
.col.as-tree{width:min(900px,100%);gap:0}
.node{display:flex;flex-direction:column;gap:8px;min-width:0}
.as-tree .node{margin-inline-start:calc(var(--depth) * 28px);padding:0 0 22px;padding-inline-start:18px;border-inline-start:1px solid var(--accent-line)}
.as-tree .node :deep(.gcard){max-width:460px}
.lineage{display:inline-flex;align-items:center;gap:6px;font-size:12px;color:var(--accent)}
.lineage svg.lucide{width:14px;height:14px}
.blank{margin-block:auto}
.dock{position:sticky;bottom:0;z-index:13;display:flex;flex-direction:column;align-items:center;gap:8px;padding:10px 24px 20px;background:linear-gradient(to bottom,transparent,var(--bg) 26px)}
.dock>*{width:min(720px,100%)}
.source{display:flex;align-items:center;gap:14px;padding:10px;border-radius:var(--r);background:var(--s2)}
.source img{width:84px;height:84px;object-fit:cover;border-radius:var(--r2);flex:none}
.source p{font-size:13px;color:var(--muted);max-height:84px;overflow-y:auto;white-space:pre-wrap;overflow-wrap:anywhere}
.help{color:var(--muted);font-size:13.5px;line-height:1.8}
@media (max-width:860px){
 .top{min-height:calc(52px + env(safe-area-inset-top,0px));padding:env(safe-area-inset-top,0px) max(8px,env(safe-area-inset-right,0px)) 0 max(8px,env(safe-area-inset-left,0px));gap:4px}
 .back{display:inline-flex}
 .title{flex:1}
 .title h1{font-size:15px;font-weight:700}
 .title .pill{display:none}
 .new{display:none}
 .menu .phone{display:flex}
 .thread{padding:16px 16px 6px}
 .col{gap:20px}
 .col.as-timeline{grid-template-columns:1fr}
 .as-tree .node{margin-inline-start:calc(var(--depth) * 10px);padding-inline-start:10px}
 .dock{padding:6px max(10px,env(safe-area-inset-right,0px)) calc(10px + env(safe-area-inset-bottom,0px)) max(10px,env(safe-area-inset-left,0px))}
}
</style>
