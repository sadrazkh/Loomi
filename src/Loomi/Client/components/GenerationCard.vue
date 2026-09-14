<script setup>
import { Pencil, GitBranch, Download, ExternalLink, Image, LoaderCircle, AlertTriangle } from 'lucide-vue-next';
defineProps({ generation:Object, t:Function, number:Number, parentNumber:Number, locale:String });
defineEmits(['edit','branch','preview']);
</script>
<template><article class="generation-card" :class="{failed:generation.status === 'Failed'}">
 <div class="card-meta"><span class="mono">{{ String(number).padStart(2,'0') }}</span><span>{{ t(generation.operation) }}</span><span v-if="parentNumber" class="muted">↳ {{ t('parent') }} {{ String(parentNumber).padStart(2,'0') }}</span><span class="status" :data-status="generation.status">{{ t(generation.status) }}</span></div>
 <button v-if="generation.imageUrl" class="image-frame" :aria-label="t('preview')" @click="$emit('preview',generation)"><img :src="generation.imageUrl" :alt="generation.prompt" loading="lazy"></button>
 <div v-else class="image-placeholder"><AlertTriangle v-if="generation.status==='Failed'"/><LoaderCircle v-else class="spin"/><span>{{ t(generation.status) }}</span></div>
 <div class="card-body"><p class="prompt-text" dir="auto">{{ generation.prompt }}</p><p v-if="generation.errorMessage" class="error-text">{{ t(generation.errorMessage) }} <small>{{ t('retryHint') }}</small></p><time>{{ new Date(generation.createdAt).toLocaleString(locale) }}</time></div>
 <footer><button :disabled="!generation.imageUrl" @click="$emit('edit',generation)"><Pencil/>{{ t('edit') }}</button><button :disabled="!generation.imageUrl" @click="$emit('branch',generation)"><GitBranch/>{{ t('branch') }}</button><span class="grow"/><a v-if="generation.imageUrl" class="icon-button" :href="generation.imageUrl" :download="`loomi-${generation.id}`" :aria-label="t('download')"><Download/></a><a v-if="generation.conversationUrl" class="icon-button" :href="generation.conversationUrl" target="_blank" rel="noopener noreferrer" :aria-label="t('viewChat')"><ExternalLink/></a></footer>
</article></template>
