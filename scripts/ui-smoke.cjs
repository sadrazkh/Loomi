// Run against the local Development host: LOOMI_TEST_KEY=<key> node scripts/ui-smoke.cjs
const { chromium } = require('../src/Loomi/bin/Debug/net10.0/.playwright/package');
const assert = require('node:assert/strict');
const fs = require('node:fs');
(async()=>{
 const browser=await chromium.launch({headless:true});
 const page=await browser.newPage({viewport:{width:1440,height:1000}});
 const errors=[];
 page.on('pageerror',e=>errors.push(e.message));
 try {
  await page.goto('http://localhost:5080');
  await page.getByLabel('Workspace access key',{exact:true}).fill(process.env.LOOMI_TEST_KEY);
  await page.getByRole('button',{name:'Unlock',exact:true}).click();
  await page.getByRole('heading',{name:'Your creative workspace'}).waitFor();
  await page.getByText('Live updates',{exact:true}).waitFor();
  fs.mkdirSync('test-results',{recursive:true});
  await page.screenshot({path:'test-results/workspace-en.png',fullPage:true});
  await page.getByRole('button',{name:'فا',exact:true}).click();
  assert.equal(await page.locator('html').getAttribute('dir'),'rtl');
  await page.screenshot({path:'test-results/workspace-fa.png',fullPage:true});
  await page.getByRole('button',{name:'پوسته',exact:true}).click();
  assert.equal(await page.locator('html').getAttribute('data-theme'),'light');
  await page.setViewportSize({width:390,height:844});
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
  await page.getByRole('button',{name:'نمایش یا بستن منو'}).click();
  await page.getByRole('button',{name:'تنظیمات',exact:true}).click();
  await page.getByRole('heading',{name:'تنظیمات',exact:true}).waitFor();
  await page.screenshot({path:'test-results/settings-fa-mobile.png',fullPage:true,animations:'disabled'});
  await page.getByRole('button',{name:'اتصال ChatGPT',exact:true}).click();
  await page.getByRole('dialog').waitFor();
  await page.keyboard.press('Escape');
  assert.equal(await page.getByRole('dialog').count(),0);
  await page.reload();
  assert.equal(await page.locator('html').getAttribute('dir'),'rtl');
  assert.equal(await page.locator('html').getAttribute('data-theme'),'light');
  const wsStatus = await page.evaluate(async()=>{
    const s=await fetch('/api/session').then(r=>r.json());
    const r=await fetch('/hubs/status/negotiate?negotiateVersion=1',{method:'POST',headers:{'X-CSRF-TOKEN':s.csrfToken}});
    return r.status;
  });
  assert.equal(wsStatus,200);
  assert.deepEqual(errors,[]);
  console.log('UI smoke passed: real login, EN/FA, RTL, themes, mobile layout, modal keyboard dismissal, preference persistence; no runtime errors.');
 } finally { await browser.close(); }
})().catch(e=>{console.error(e);process.exitCode=1;});
