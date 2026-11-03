// Strings of the public intro page and its settings. Every key goes in both blocks.
// The answers under "questions and privacy" are promises about what the code does: GenerationWorker.SweepUploadsAsync, ProjectsController.Delete, Ownership and
// Credits.RefundAsync are what they were checked against, so a change to any of those means reading them again.
export default {
 en: {
  laSignIn:'Sign in', laAbout:'About Loomi',
  laHero:'Describe it or send a photo, and we make the picture with AI.',
  laLedeOpen:'In Telegram or here on the site. You pay per picture, in credits.',
  laLede:'You pay per picture, in credits.',
  laStart:'Start in Telegram', laStartGift:'Start in Telegram with {0} gift pictures', laStartGiftOne:'Start in Telegram with one gift picture',

  laMakeTitle:'What you can make',
  laMakeHelp:'A few ready-made looks. Anything else, you can simply describe.',
  laFilter:'Filter by category', laAll:'All',

  laHowTitle:'How it works',
  laStep1:'Say what you want', laStep1Body:'Describe the picture in a few words, or send a photo and say what to do with it.',
  laStep2:'Give it about a minute', laStep2Body:'Most pictures take about a minute, a little longer when the queue is busy. It arrives in the same chat.',
  laStep3:'Say what to change', laStep3Body:'Write it in the same chat and the picture is made again. No need to send the photo a second time.',

  laPricesTitle:'Prices', laPick:'Our pick', laBuy:'Where to buy', laOpenContact:'Open',

  laFaqTitle:'Questions and privacy',
  laQ1:'Where do my photos go?',
  laA1:'They are sent to ChatGPT to make the picture, or to Google’s Gemini if that picture is made with Gemini. ChatGPT keeps them in the conversation the picture was made in, and deleting a chat here does not remove them from there.',
  laQ2:'How long do you keep them?',
  laA2:'They stay on our server until you delete the chat, which you do from inside it on the site. The pictures go at once and the photos you sent within about a day.',
  laQ3:'Who can see them?',
  laA3:'The owner of this service can see your chats, photos and pictures. Other people using it cannot. Please do not send anything you would not want seen.',
  laQ4:'What if a picture is not made?',
  laA4:'Your credits come back by themselves. The same goes for a request you cancel, or one answered in words instead of a picture.',
  laQ5:'Is Loomi made by OpenAI?',
  laA5:'No. Loomi is a separate service and has no connection with OpenAI. It uses ChatGPT to make the pictures.',

  siIntro:'What people see before they sign in. It shows only what you enter here: with no prices, there is no prices section.',
  siOpen:'Open the public page', siAddress:'The address for adverts:',
  siSignup:'Sign-up and its gift are switched on the Bot page.', siGoBot:'Open the Bot page',
  siPrices:'Prices', siPricesHelp:'Up to {0} rows, in your own words: the page shows a price exactly as you write it.', siNoRows:'No prices yet.',
  siName:'Name', siDetail:'Details (optional)', siPrice:'Price', siHighlight:'Mark this one', siAdd:'Add a row', siRemove:'Remove this row',
  siContact:'Where to buy', siContactHelp:'A Telegram handle such as @loomi_support, or an address starting with https://. Leave it empty to show nothing.',
  siSave:'Save', siSaved:'Saved. The public page catches up within a minute.',
  InvalidContact:'Where to buy must be a Telegram handle such as @loomi_support, or an address starting with https://, up to 100 characters.',
  InvalidPrices:'Check the prices: at most 6 rows, each with a name up to 60 characters and a price up to 40, details up to 200, and only one marked. A row full of emoji can also be too long.',
 },
 fa: {
  laSignIn:'ورود', laAbout:'درباره‌ی Loomi',
  laHero:'توضیح بدهید یا عکس بفرستید؛ تصویرتان را با هوش مصنوعی می‌سازیم.',
  laLedeOpen:'در تلگرام یا همین سایت. هزینه‌ی هر تصویر را با اعتبار می‌پردازید.',
  laLede:'هزینه‌ی هر تصویر را با اعتبار می‌پردازید.',
  laStart:'شروع در تلگرام', laStartGift:'شروع در تلگرام با {0} تصویر هدیه', laStartGiftOne:'شروع در تلگرام با یک تصویر هدیه',

  laMakeTitle:'چه چیزهایی می‌توانید بسازید',
  laMakeHelp:'چند نمونه از قالب‌های آماده. هر چیز دیگری را هم کافی است توضیح بدهید.',
  laFilter:'فیلتر بر اساس دسته', laAll:'همه',

  laHowTitle:'چطور کار می‌کند',
  laStep1:'بگویید چه می‌خواهید', laStep1Body:'تصویر را در چند کلمه توضیح بدهید، یا عکسی بفرستید و بگویید با آن چه کنیم.',
  laStep2:'حدود یک دقیقه صبر کنید', laStep2Body:'بیشتر تصویرها حدود یک دقیقه طول می‌کشند و اگر صف شلوغ باشد کمی بیشتر. تصویر در همان گفت‌وگو به دستتان می‌رسد.',
  laStep3:'بگویید چه چیزی عوض شود', laStep3Body:'در همان گفت‌وگو بنویسید تا تصویر دوباره ساخته شود. لازم نیست عکس را دوباره بفرستید.',

  laPricesTitle:'قیمت‌ها', laPick:'پیشنهاد ما', laBuy:'برای خرید', laOpenContact:'باز کردن',

  laFaqTitle:'پرسش‌ها و حریم خصوصی',
  laQ1:'عکس‌هایم کجا می‌روند؟',
  laA1:'برای ساخت تصویر به ChatGPT فرستاده می‌شوند، یا به Gemini گوگل اگر آن تصویر با Gemini ساخته شود. ChatGPT آن‌ها را در همان گفت‌وگویی که تصویر در آن ساخته شده نگه می‌دارد و حذف گفت‌وگو در Loomi آن‌ها را از آنجا پاک نمی‌کند.',
  laQ2:'تا کی نگهشان می‌دارید؟',
  laA2:'روی سرور ما می‌مانند تا وقتی گفت‌وگو را حذف کنید؛ این کار را از داخل همان گفت‌وگو در سایت انجام می‌دهید. تصویرها همان لحظه پاک می‌شوند و عکس‌هایی که فرستاده‌اید حدود یک روز بعد.',
  laQ3:'چه کسی آن‌ها را می‌بیند؟',
  laA3:'صاحب این سرویس می‌تواند گفت‌وگوها، عکس‌ها و تصویرهای شما را ببیند، اما کاربران دیگر نمی‌توانند. چیزی را که نمی‌خواهید دیده شود نفرستید.',
  laQ4:'اگر تصویر ساخته نشد چه می‌شود؟',
  laA4:'اعتبارتان خودبه‌خود برمی‌گردد. درخواستی که لغو کنید یا جوابی که به‌جای تصویر متن باشد هم همین‌طور است.',
  laQ5:'آیا Loomi محصول OpenAI است؟',
  laA5:'نه. Loomi سرویسی جداست و ارتباطی با OpenAI ندارد؛ برای ساخت تصویرها از ChatGPT استفاده می‌کند.',

  siIntro:'چیزی که مردم پیش از ورود می‌بینند. فقط همان چیزی را نشان می‌دهد که اینجا می‌نویسید: اگر قیمتی ننویسید، بخش قیمت‌ها اصلاً نمایش داده نمی‌شود.',
  siOpen:'باز کردن صفحه‌ی عمومی', siAddress:'نشانی برای تبلیغ:',
  siSignup:'ثبت‌نام و هدیه‌اش از صفحه‌ی ربات روشن و خاموش می‌شوند.', siGoBot:'باز کردن صفحه‌ی ربات',
  siPrices:'قیمت‌ها', siPricesHelp:'تا {0} ردیف، با کلمات خودتان: صفحه هر قیمت را دقیقاً همان‌طور که می‌نویسید نشان می‌دهد.', siNoRows:'هنوز قیمتی ننوشته‌اید.',
  siName:'نام', siDetail:'توضیح (اختیاری)', siPrice:'قیمت', siHighlight:'این یکی برجسته شود', siAdd:'افزودن ردیف', siRemove:'حذف این ردیف',
  siContact:'محل خرید', siContactHelp:'شناسه‌ی تلگرام مثل ‎@loomi_support، یا نشانی‌ای که با ‎https:// شروع شود. اگر خالی بماند چیزی نشان داده نمی‌شود.',
  siSave:'ذخیره', siSaved:'ذخیره شد. صفحه‌ی عمومی تا یک دقیقه‌ی دیگر به‌روز می‌شود.',
  InvalidContact:'محل خرید باید شناسه‌ی تلگرام مثل ‎@loomi_support یا نشانی‌ای باشد که با ‎https:// شروع می‌شود، حداکثر ۱۰۰ نویسه.',
  InvalidPrices:'قیمت‌ها را بررسی کنید: حداکثر ۶ ردیف، هر کدام با نام تا ۶۰ نویسه و قیمت تا ۴۰ نویسه، توضیح تا ۲۰۰ نویسه و فقط یکی برجسته. ردیف پر از ایموجی هم ممکن است زیادی بلند شود.',
 }
};
