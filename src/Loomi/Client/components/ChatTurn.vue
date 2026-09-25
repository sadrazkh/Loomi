<script setup>
import { Pencil, GitBranch, Download, ExternalLink, LoaderCircle, Bot, X, LayoutTemplate } from 'lucide-vue-next';
import { queueText } from './queue.js';
// Latest: the chat's newest picture, which the next message works on anyway, so editing it needs nothing sent again.
defineProps({ generation:Object, t:Function, number:Number, parentNumber:Number, locale:String, queue:Object, latest:Boolean });
defineEmits(['edit','branch','preview','cancel']);
const settled = g => ['Completed','Answered','Failed','Cancelled'].includes(g.status);
</script>
<template><article class="turn">
 <div class="turn-user"><div class="bubble"><div v-if="generation.inputs?.length" class="card-inputs" role="group" :aria-label="t('references')"><img v-for="(input,n) in generation.inputs" :key="n" :src="input.url" alt="" loading="lazy"></div><span v-if="generation.templateTitle" class="template-chip"><LayoutTemplate/><span>{{ t('templateLabel') }}: <bdi>{{ generation.templateTitle }}</bdi></span></span><p v-if="generation.prompt" class="prompt-text" dir="auto">{{ generation.prompt }}</p></div><time :datetime="generation.createdAt">{{ new Date(generation.createdAt).toLocaleString(locale) }}</time></div>
 <div class="turn-assistant"><span class="turn-avatar" aria-hidden="true"><Bot/></span><div class="bubble" :class="{failed:generation.status==='Failed'}">
  <div class="card-meta"><span class="mono">{{ String(number).padStart(2,'0') }}</span><span>{{ t(generation.operation) }}</span><span v-if="parentNumber" class="muted">↳ {{ t('parent') }} {{ String(parentNumber).padStart(2,'0') }}</span><span class="status" :data-status="generation.status">{{ t(generation.status) }}</span></div>
  <button v-if="generation.imageUrl" class="image-frame" :aria-label="t('preview')" @click="$emit('preview',generation)"><img :src="generation.imageUrl" :alt="generation.prompt||generation.templateTitle||''" loading="lazy"></button>
  <p v-else-if="generation.replyText" class="turn-reply" dir="auto">{{ generation.replyText }}</p>
  <div v-else-if="!settled(generation)" class="turn-progress" role="status"><LoaderCircle class="spin"/><span>{{ t(generation.status) }}<small v-if="generation.status==='Queued'&&queue" class="queue-note">{{ queueText(queue,t,locale) }}</small></span></div>
  <p v-else class="error-text turn-error">{{ t(generation.errorMessage||generation.status) }} <small v-if="generation.errorMessage">{{ t('retryHint') }}</small></p>
  <footer v-if="generation.imageUrl||generation.conversationUrl||!settled(generation)"><template v-if="generation.imageUrl"><button :title="t(latest?'chatEditHint':'chatEditOlderHint')" @click="$emit('edit',generation)"><Pencil/>{{ t('edit') }}</button><button :title="t('chatBranchHint')" @click="$emit('branch',generation)"><GitBranch/>{{ t('chatNew') }}</button></template><button v-if="!settled(generation)" class="danger" @click="$emit('cancel',generation)"><X/>{{ t('cancel') }}</button><span class="grow"/><a v-if="generation.imageUrl" class="icon-button" :href="generation.imageUrl" :download="`loomi-${generation.id}`" :aria-label="t('download')"><Download/></a><a v-if="generation.conversationUrl" class="icon-button" :href="generation.conversationUrl" target="_blank" rel="noopener noreferrer" :aria-label="t('viewChat')"><ExternalLink/></a></footer>
 </div></div>
</article></template>
