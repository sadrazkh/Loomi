<script setup>
import { ref, onMounted } from 'vue';
import { Plus, Trash2, Save, ExternalLink, CircleCheck, Bot } from 'lucide-vue-next';
import LandingView from '../LandingView.vue';
import { t, tf, api, action, busy, go } from '../../core';
// What the intro page says that only the owner can say: the prices and where to buy. The page shows nothing the owner did not write here.
const MAX_ROWS = 6;
const rows = ref([]), contact = ref(''), loaded = ref(false), failed = ref(false), saved = ref(false);
let nextKey = 0;
const row = (r = {}) => ({ key: nextKey++, name: r.name || '', detail: r.detail || '', price: r.price || '', highlight: !!r.highlight });
const put = s => { rows.value = s.prices.map(row); contact.value = s.contact || ''; };
onMounted(async () => {
 try { put(await api('/api/site/settings')); }
 catch { failed.value = true; }
 finally { loaded.value = true; }
});
const blank = r => !r.name.trim() && !r.detail.trim() && !r.price.trim();
// A row left wholly empty is no row; one half filled goes to the server, which says what is wrong with it.
const save = () => action(async () => {
 const prices = rows.value.filter(r => !blank(r)).map(({ name, detail, price, highlight }) => ({ name, detail, price, highlight }));
 put(await api('/api/site/settings', 'PUT', { prices, contact: contact.value }));
 saved.value = true;
});
const add = () => { rows.value.push(row()); saved.value = false; };
const remove = r => { rows.value = rows.value.filter(x => x !== r); saved.value = false; };
// Only one is marked: marking another moves the mark, rather than leaving a state the server would refuse.
const mark = (r, on) => { for (const x of rows.value) x.highlight = false; r.highlight = on; };

// Signed in, the address shows the workspace and the intro page never does. So the link to it asks this page to show it, over everything, in a new tab, and
// the plain address beside it is the one to give out.
const previewing = new URLSearchParams(location.search).has('preview');
const previewLink = `${location.pathname}?preview#/admin/site`;
const address = `${location.origin}/`;
</script>

<template>
 <Teleport v-if="previewing" to="body"><div class="preview"><LandingView/></div></Teleport>
 <div class="view view-narrow site">
  <header class="view-head">
   <div><h1>{{ t('shSite') }}</h1><p>{{ t('siIntro') }}</p></div>
   <a class="btn" :href="previewLink" target="_blank" rel="noopener"><ExternalLink/>{{ t('siOpen') }}</a>
  </header>
  <p class="where"><span class="muted-text">{{ t('siAddress') }}</span> <bdi class="address" dir="ltr">{{ address }}</bdi></p>
  <div class="notice"><Bot/><span class="grow">{{ t('siSignup') }}</span><button type="button" class="btn btn-sm" @click="go('admin','bot')">{{ t('siGoBot') }}</button></div>

  <div v-if="!loaded" class="skel" style="height:220px"></div>
  <p v-else-if="failed" class="faint-text">{{ t('adCouldNotLoad') }}</p>
  <form v-else class="stack" @input="saved=false" @submit.prevent="save">
   <section class="card" aria-labelledby="si-prices">
    <div class="card-head"><h2 id="si-prices">{{ t('siPrices') }}</h2></div>
    <p class="field-help">{{ tf('siPricesHelp', MAX_ROWS) }}</p>
    <p v-if="!rows.length" class="muted-text">{{ t('siNoRows') }}</p>
    <div v-for="r in rows" :key="r.key" class="price-row">
     <label class="field"><span class="field-label">{{ t('siName') }}</span><input v-model="r.name" class="input" dir="auto" maxlength="60"></label>
     <label class="field"><span class="field-label">{{ t('siPrice') }}</span><input v-model="r.price" class="input" dir="auto" maxlength="40"></label>
     <label class="field wide"><span class="field-label">{{ t('siDetail') }}</span><input v-model="r.detail" class="input" dir="auto" maxlength="200"></label>
     <div class="row-foot wide">
      <label class="check"><input type="checkbox" :checked="r.highlight" @change="mark(r, $event.target.checked)">{{ t('siHighlight') }}</label>
      <button type="button" class="btn btn-ghost btn-sm" @click="remove(r)"><Trash2/>{{ t('siRemove') }}</button>
     </div>
    </div>
    <div><button v-if="rows.length < MAX_ROWS" type="button" class="btn btn-sm" @click="add"><Plus/>{{ t('siAdd') }}</button></div>
   </section>

   <section class="card" aria-labelledby="si-contact">
    <div class="card-head"><h2 id="si-contact">{{ t('siContact') }}</h2></div>
    <div class="field">
     <input v-model="contact" class="input" dir="ltr" :aria-labelledby="'si-contact'" aria-describedby="si-contact-help" maxlength="100" autocomplete="off" spellcheck="false" placeholder="@loomi_support">
     <p id="si-contact-help" class="field-help">{{ t('siContactHelp') }}</p>
    </div>
   </section>

   <div class="actions">
    <span v-if="saved" class="ok" role="status"><CircleCheck/>{{ t('siSaved') }}</span>
    <button class="btn btn-primary" :disabled="busy"><Save/>{{ t('siSave') }}</button>
   </div>
  </form>
 </div>
</template>

<style scoped>
.preview{position:fixed;inset:0;z-index:100;overflow-y:auto;background:var(--bg)}
.where{font-size:13.5px;margin-top:-12px;display:flex;flex-wrap:wrap;gap:4px 8px}
.address{user-select:all;overflow-wrap:anywhere;min-width:0}
.notice{align-items:center;flex-wrap:wrap}
.notice svg.lucide{margin-top:0}
.card h2{font-size:15.5px}
.price-row{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px;padding-top:14px;border-top:1px solid var(--line)}
.wide{grid-column:1/-1}
.row-foot{display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:8px}
.actions{display:flex;align-items:center;justify-content:flex-end;flex-wrap:wrap;gap:12px}
.ok{display:inline-flex;align-items:center;gap:6px;color:var(--ok);font-size:13.5px;margin-inline-end:auto}
@media (max-width:560px){
 .price-row{grid-template-columns:minmax(0,1fr)}
}
</style>
