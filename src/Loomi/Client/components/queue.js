/** Where a waiting request stands, as the person who sent it sees it: how many are ahead and roughly how long, never whose requests they are. */
export function queueText(q, t, locale) {
  if (!q) return '';
  const n = v => Number(v).toLocaleString(locale);
  const place = q.ahead === 0 ? t('queueNext') : q.ahead === 1 ? t('queueAheadOne') : t('queueAhead').replace('{n}', n(q.ahead));
  const when = q.etaSeconds == null ? t('queueNoAccount') : q.etaSeconds < 60 ? t('queueSoon') : t('queueEta').replace('{m}', n(Math.ceil(q.etaSeconds / 60)));
  return `${place} · ${when}`;
}
