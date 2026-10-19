import { t, tf } from '../core';
/** Where a waiting request stands, as the person who sent it sees it: how many are ahead and roughly how long, never whose requests they are. */
export function queueText(q) {
  if (!q || q.ahead == null) return '';
  const place = q.ahead === 0 ? t('stQueueNext') : q.ahead === 1 ? t('stQueueOne') : tf('stQueueAhead', q.ahead);
  const wait = q.etaSeconds == null ? t('stQueueNoAccount') : q.etaSeconds < 60 ? t('stQueueSoon') : tf('stQueueEta', Math.ceil(q.etaSeconds / 60));
  return place + t('stSep') + wait;
}
// The four steps a person sees: sent, in the queue, being made, ready. The run's own finer states fold into them.
export const STEPS = ['stStepSent', 'stStepQueue', 'stStepMaking', 'stStepReady'];
const PHASES = {
  Queued: 'queue',
  OpeningBrowser: 'sending', OpeningChatGPT: 'sending', SendingPrompt: 'sending', WaitingForResponse: 'sending',
  GeneratingImage: 'making', DownloadingImage: 'making',
};
/** queue, sending or making; a state added on the server later reads as sending until it is named here. */
export const phaseOf = status => PHASES[status] || 'sending';
/** Which of STEPS is the current one: the queue is the second, sending and making both light the third. */
export const stepOf = status => phaseOf(status) === 'queue' ? 1 : 2;
