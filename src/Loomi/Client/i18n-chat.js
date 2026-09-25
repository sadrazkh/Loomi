// Strings for one project, one conversation. Every key goes in both blocks.
// ProjectBusy is redefined here on purpose: it used to be met only when deleting, and now it is also what a message sent too early meets.
export default {
 en: {
  chatContinues:'Everything you write here continues this chat', chatStarts:'Your first message starts this chat', chatWaiting:'Waiting for the answer to your last message…',
  chatNew:'New chat', chatNewHint:'Start a new chat, which is a new project. Whatever you have typed becomes its first message.',
  chatBranchHint:'Start a new chat from this picture; this chat stays as it is', chatBranchTitle:'New chat from this picture', chatBranchHelp:'This picture goes with your message to start a new chat, a project of its own. This chat stays as it is.',
  chatEditHint:'Write your change in the box below: it continues this chat, where this picture already is', chatEditOlderHint:'An earlier picture: it is sent again with your change, in this chat',
  chatEditOlderHelp:'This is not the chat’s latest picture, so it goes along with your change and the chat carries on from it.',
  chatStart:'Start chat', chatStartHelp:'Each project is one chat with the AI. Write its first message to start right away, or give just a title to open it empty.',
  chatFirstMessage:'First message', chatTitleOptional:'Title (optional)', chatTitleHint:'Left empty, the chat is named by the opening words of its first message.',
  chatEmptyBody:'Connect your ChatGPT session, then start your first chat. Each project is one conversation, and everything made in it stays together.',
  ProjectBusy:'This chat is still working on a message. Wait for the answer, or cancel it.',
 },
 fa: {
  chatContinues:'هر چه این‌جا بنویسید همین گفتگو را ادامه می‌دهد', chatStarts:'نخستین پیام شما این گفتگو را آغاز می‌کند', chatWaiting:'در انتظار پاسخ پیام قبلی…',
  chatNew:'گفتگوی تازه', chatNewHint:'یک گفتگوی تازه، یعنی یک پروژهٔ تازه، شروع کنید. هر چه نوشته‌اید نخستین پیام آن می‌شود.',
  chatBranchHint:'با این تصویر یک گفتگوی تازه شروع کنید؛ این گفتگو همان‌طور که هست می‌ماند', chatBranchTitle:'گفتگوی تازه با این تصویر', chatBranchHelp:'این تصویر همراه پیام شما یک گفتگوی تازه را آغاز می‌کند که پروژهٔ جداگانه‌ای است. این گفتگو دست‌نخورده می‌ماند.',
  chatEditHint:'تغییر را در کادر پایین بنویسید؛ همین گفتگو ادامه پیدا می‌کند و این تصویر از قبل در آن هست', chatEditOlderHint:'تصویری قدیمی‌تر: همراه تغییر شما دوباره در همین گفتگو فرستاده می‌شود',
  chatEditOlderHelp:'این آخرین تصویر گفتگو نیست؛ پس همراه تغییر شما فرستاده می‌شود و گفتگو از روی آن ادامه پیدا می‌کند.',
  chatStart:'شروع گفتگو', chatStartHelp:'هر پروژه یک گفتگو با هوش مصنوعی است. نخستین پیامش را بنویسید تا همین حالا شروع شود، یا فقط عنوانی بدهید تا خالی باز شود.',
  chatFirstMessage:'نخستین پیام', chatTitleOptional:'عنوان (اختیاری)', chatTitleHint:'اگر خالی بماند، نام گفتگو از نخستین واژه‌های پیامش گرفته می‌شود.',
  chatEmptyBody:'به نشست ChatGPT متصل شوید و نخستین گفتگویتان را شروع کنید. هر پروژه یک گفتگوست و هر چه در آن ساخته می‌شود کنار هم می‌ماند.',
  ProjectBusy:'این گفتگو هنوز روی یک پیام کار می‌کند. منتظر پاسخ بمانید یا آن را لغو کنید.',
 },
};
