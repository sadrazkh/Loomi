<script setup>
import { ref, watch, onMounted } from 'vue';
import { Globe, Send, Code, CircleCheck, CircleX, Ban, LoaderCircle, MessageSquareText, LayoutTemplate, FolderOpen, Download, ChevronDown, Layers, Package, MessagesSquare } from 'lucide-vue-next';
import Modal from '../../components/Modal.vue';
import { t, tf, api } from '../../core';
import { dateTime } from './kit';
// One person's requests, newest first, twenty at a time: the picture when there is one, what they asked, how it went, where from, what it cost and
// which package paid. A picture opens whole in a dialog.
const props = defineProps({ userId: { type: String, required: true } });
const PAGE = 20;
const CHANNELS = { Web: [Globe, 'channelWeb'], Bot: [Send, 'channelBot'], Api: [Code, 'channelApi'] };
const BADGES = { Completed: ['pill-ok', CircleCheck], Answered: ['', MessageSquareText], Failed: ['pill-bad', CircleX], Cancelled: ['pill-muted', Ban] };
const items = ref([]), loaded = ref(false), more = ref(false), busy = ref(false), failed = ref(false), expanded = ref(new Set()), viewing = ref(null);

async function load(reset) {
 const id = props.userId; if (busy.value) return; busy.value = true;
 try {
  const page = await api(`/api/usage/users/${id}/requests?skip=${reset ? 0 : items.value.length}&take=${PAGE}`);
  if (props.userId !== id) return;
  // A request made since the last page pushes the older ones down a place, so the next page can repeat one already shown.
  const seen = new Set(reset ? [] : items.value.map(r => r.id));
  items.value = [...(reset ? [] : items.value), ...page.filter(r => !seen.has(r.id))];
  more.value = page.length === PAGE; failed.value = false;
 } catch { failed.value = true; }
 finally { loaded.value = true; busy.value = false; }
}
onMounted(() => load(true));
watch(() => props.userId, () => { items.value = []; loaded.value = false; load(true); });

const badge = s => BADGES[s] ?? ['pill-warn', LoaderCircle];
const settled = s => s in BADGES;
const channel = c => CHANNELS[c] ?? CHANNELS.Web;
const imageOf = r => `/api/generations/${r.id}/image`;
const alt = r => r.prompt ? (r.prompt.length > 140 ? r.prompt.slice(0, 140).trimEnd() + '…' : r.prompt) : r.templateTitle || t('usageImage');
// Only a long prompt or a reply gets the toggle; a short one is already whole.
const long = r => (r.prompt?.length ?? 0) > 160 || (r.prompt?.split('\n').length ?? 0) > 3 || (r.status === 'Answered' && (r.replyText?.length ?? 0) > 120);
function toggle(r) { const next = new Set(expanded.value); if (!next.delete(r.id)) next.add(r.id); expanded.value = next; }
</script>

<template>
 <div class="reqs">
  <p class="faint-text">{{ t('usageRequestsHelp') }}</p>
  <div v-if="!loaded" class="stack"><div v-for="i in 3" :key="i" class="skel" style="height:84px"></div></div>
  <div v-else-if="!items.length" class="quiet"><MessagesSquare/><span>{{ t(failed ? 'adCouldNotLoad' : 'usageNoRequests') }}</span></div>
  <ol v-else class="list-reset">
   <li v-for="r in items" :key="r.id" class="req" :class="{ open: expanded.has(r.id) }">
    <button v-if="r.hasImage" class="thumb" :aria-label="t('usageOpenImage')" :title="t('usageOpenImage')" @click="viewing = r"><img :src="imageOf(r)" :alt="alt(r)" loading="lazy"></button>
    <span v-else class="thumb empty" aria-hidden="true"><component :is="badge(r.status)[1]" :class="{ spin: !settled(r.status) }"/></span>
    <div class="body">
     <div class="meta"><time :datetime="r.createdAt">{{ dateTime(r.createdAt) }}</time>{{ t('adSep') }}<FolderOpen class="fi"/><bdi>{{ r.projectTitle }}</bdi>{{ t('adSep') }}{{ t(r.operation) }}</div>
     <p v-if="r.prompt" class="prompt" dir="auto">{{ r.prompt }}</p>
     <p v-if="r.status === 'Answered' && r.replyText" class="reply" dir="auto">{{ r.replyText }}</p>
     <p v-if="r.status === 'Failed' && r.errorMessage" class="field-error">{{ t(r.errorMessage) }} <code v-if="t(r.errorMessage) !== r.errorMessage" dir="ltr">{{ r.errorMessage }}</code></p>
     <div class="foot">
      <span class="pill" :class="badge(r.status)[0]"><component :is="badge(r.status)[1]" :class="{ spin: !settled(r.status) }"/>{{ t(r.status) }}</span>
      <span class="tag"><component :is="channel(r.channel)[0]" class="ti"/>{{ t(channel(r.channel)[1]) }}</span>
      <span v-if="r.templateTitle" class="tag"><LayoutTemplate class="ti"/><bdi>{{ r.templateTitle }}</bdi></span>
      <span class="cost"><Layers/>{{ tf('shPkgCredits', r.creditCost) }}</span>
      <span v-if="r.packageName" class="cost"><Package/><bdi>{{ r.packageName }}</bdi></span>
      <span class="grow"/>
      <button v-if="long(r)" class="btn btn-ghost btn-sm" :aria-expanded="expanded.has(r.id)" @click="toggle(r)"><ChevronDown class="flip"/>{{ t(expanded.has(r.id) ? 'usageLess' : 'usageMore') }}</button>
     </div>
    </div>
   </li>
  </ol>
  <button v-if="more" class="btn btn-sm loadmore" :disabled="busy" @click="load(false)">{{ busy ? t('shLoading') : t('usageLoadMore') }}</button>

  <Teleport to="body">
   <Modal v-if="viewing" :title="viewing.projectTitle" :close-label="t('close')" size="full" @close="viewing = null">
    <img class="full" :src="imageOf(viewing)" :alt="alt(viewing)">
    <p v-if="viewing.prompt" class="muted-text" dir="auto">{{ viewing.prompt }}</p>
    <div class="dlg-actions"><a class="btn" :href="imageOf(viewing)" :download="`loomi-${viewing.id}`"><Download/>{{ t('download') }}</a></div>
   </Modal>
  </Teleport>
 </div>
</template>

<style scoped>
.reqs{display:flex;flex-direction:column;gap:12px;min-width:0}
.list-reset{list-style:none;margin:0;padding:0;display:flex;flex-direction:column}
.quiet{display:flex;align-items:center;gap:10px;padding:16px 0;color:var(--muted);font-size:13.5px}
.quiet svg{color:var(--faint)}
.req{display:grid;grid-template-columns:64px minmax(0,1fr);gap:12px;padding:12px 0;border-top:1px solid var(--line)}
.req:first-child{border-top:0;padding-top:0}
.thumb{width:64px;height:64px;border-radius:var(--r2);overflow:hidden;border:0;padding:0;background:var(--s3);display:grid;place-items:center;color:var(--faint)}
.thumb img{width:100%;height:100%;object-fit:cover;display:block}
button.thumb:hover{outline:2px solid var(--accent-line)}
.body{display:flex;flex-direction:column;gap:6px;min-width:0}
.meta{font-size:12px;color:var(--faint);overflow-wrap:anywhere}
.fi{width:13px!important;height:13px!important;vertical-align:-2px;margin-inline-end:3px}
.prompt,.reply{font-size:13.5px;line-height:1.7;white-space:pre-wrap;overflow-wrap:anywhere;display:-webkit-box;-webkit-line-clamp:3;-webkit-box-orient:vertical;overflow:hidden}
.reply{color:var(--muted);padding-inline-start:10px;border-inline-start:2px solid var(--line2)}
.open .prompt,.open .reply{display:block;-webkit-line-clamp:unset}
.foot{display:flex;flex-wrap:wrap;align-items:center;gap:6px}
.ti{width:12px!important;height:12px!important;margin-inline-end:4px}
.cost{display:inline-flex;align-items:center;gap:4px;font-size:12px;color:var(--muted)}
.cost svg{width:13px;height:13px;color:var(--faint)}
.open .flip{transform:rotate(180deg)}
.loadmore{align-self:center}
.full{display:block;width:100%;max-height:72vh;object-fit:contain;border-radius:var(--r2);background:var(--s2)}
</style>
