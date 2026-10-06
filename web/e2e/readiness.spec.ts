import {test,expect,type Page} from '@playwright/test';
import { openPage } from './support/navigation';
// Wait for the disposable demo's background history seed before asserting current metrics.
test.beforeEach(async({request})=>{const response=await request.post('/api/telemetry/collect',{headers:{'X-Joule-Request':'1'},data:{}});expect(response.ok(),await response.text()).toBeTruthy();});
const nav = openPage;
test('the AI card reflects the saved schedule and links to the controls',async({page,request})=>{
 const payload=await(await request.get('/api/state')).json();payload.state.ai.scheduled=false;payload.ai.schedule={enabled:false,state:'Disabled',reason:'Automatic investigations are off. Run one manually or enable the schedule.',nextRunAt:null,lastAttemptAt:null,lastCompletedAt:null,runsToday:2,maxRunsPerDay:24,intervalMinutes:60};
 await page.route('**/api/state',r=>r.fulfill({json:payload}));await page.goto('/');
 const card=page.getByRole('region',{name:'AI checks'});await expect(card).toContainText('Automatic checks are off');
 // Nothing needs action, so the full-width band stays away.
 await expect(page.getByRole('region',{name:'AI checks need attention'})).toHaveCount(0);
 await card.getByRole('link',{name:'Turn on automatic checks'}).click();await expect(page.getByRole('switch',{name:'Automatic checks'})).not.toBeChecked();
 payload.state.ai.scheduled=true;const last=new Date(Date.now()-600000).toISOString(),next=new Date(Date.now()+3600000).toISOString();payload.ai.schedule={...payload.ai.schedule,enabled:true,state:'Waiting',reason:'Next automatic investigation waits for the configured interval.',lastCompletedAt:last,nextRunAt:next};
 await page.goto('/#/today');await page.reload();const clock=(t:string)=>new Intl.DateTimeFormat('en-GB',{hour:'2-digit',minute:'2-digit',hourCycle:'h23',timeZone:'Europe/London'}).format(new Date(t));
 await expect(page.getByRole('region',{name:'AI checks'})).toContainText(`Last AI check ${clock(last)} · next ${clock(next)}`);await expect(page.getByRole('region',{name:'AI checks'})).not.toContainText('daily checks used');
 // Near the limit the card says how much is used, in words; when it is used up, one band says so with its own action.
 payload.ai.schedule={...payload.ai.schedule,runsToday:20};await page.reload();await expect(page.getByRole('region',{name:'AI checks'})).toContainText(`Last AI check ${clock(last)} · next ${clock(next)} · 20 of 24 daily checks used`);
 payload.ai.schedule={...payload.ai.schedule,state:'DailyLimit',runsToday:24};await page.reload();
 const band=page.getByRole('region',{name:'AI checks need attention'});await expect(band).toContainText('Today’s 24 AI checks are used up');await expect(band.getByRole('link',{name:'Change the limit'})).toBeVisible();
 // Setup › AI checks says when Joule checks next, in the schedule's own words.
 await page.goto('/#/setup/ai');const schedule=page.getByRole('region',{name:'When Joule checks'});await expect(schedule).toContainText('Automatic');await expect(schedule).toContainText('Next automatic investigation waits for the configured interval.');
});
test('Data starts with today in the configured zone and does not count future hours as missing',async({page,request})=>{
 const st=await(await request.get('/api/telemetry/status')).json();st.timeZone='Pacific/Auckland';st.firstObservationAt=new Date(Date.now()-3600000).toISOString();await page.route('**/api/telemetry/status',r=>r.fulfill({json:st}));await page.goto('/');await nav(page,'Energy');
 // Today is midnight to now in the household's zone (Auckland here), never the browser's day or a rolling 24 hours.
 const call=page.waitForRequest(r=>r.url().includes('/api/telemetry/summary?'));await page.getByRole('group',{name:'Period'}).getByRole('button',{name:'Today',exact:true}).click();const url=new URL((await call).url());const end=url.searchParams.get('to')!,start=url.searchParams.get('from')!;expect(Math.abs(Date.parse(end)-Date.now())).toBeLessThan(30000);
 expect(new Intl.DateTimeFormat('en-GB',{timeZone:st.timeZone,hour:'2-digit',minute:'2-digit',hourCycle:'h23'}).format(new Date(start))).toBe('00:00');await expect(page.locator('.energy-asof')).toContainText('up to');
});
test('a plan that is all still ahead reads as a plan, not a table of missing measurements',async({page,request})=>{
 const p=await(await request.get('/api/state')).json(),time=new Date(Date.now()+3600000).toISOString();const slot={time,durationMinutes:30,action:'Charge',loadForecast:1,pvForecast:0,socForecast:40,loadActual:null,pvActual:null,socActual:null,importRate:7,exportRate:15,cost:.07};p.plan={id:'readiness-future',at:new Date().toISOString(),collectedAt:new Date().toISOString(),source:'Predbat',slots:[slot]};
 await page.route('**/api/state',r=>r.fulfill({json:p}));await page.route('**/api/telemetry/plans/readiness-future/alternative',r=>r.fulfill({json:{available:false,reason:'Predbat has not published a LoadML forecast.',methodology:'Fixture',slots:[]}}));
 await page.goto('/');await nav(page,'Plan');const next=page.getByRole('region',{name:"What's next, window by window"});await expect(next.locator('.plan-badge').filter({hasText:'Charge'})).toBeVisible();await expect(next).toContainText('7p');
 await expect(page.locator('main')).not.toContainText(/Upcoming|Unavailable|Not measured|This plan is all still ahead|see the README/);
});
test('overview cards expose real coverage and battery with responsive presentation',async({page})=>{
 await page.goto('/');await expect(page.getByRole('article',{name:'Battery',exact:true}).getByRole('img',{name:/Battery level/})).toBeVisible();await expect(page.getByRole('article',{name:'Battery',exact:true}).locator('.stat-value')).toHaveText(/\d+%|—/);await expect(page.locator('.coverage-track')).toHaveCount(0);await expect(page.locator('.today-tiles')).not.toContainText(/of today measured|not compared/);
 await page.screenshot({path:'../.cache/screenshots/overview-refined-desktop.png',fullPage:true});await page.setViewportSize({width:390,height:844});await expect.poll(()=>page.evaluate(()=>Math.max(document.documentElement.scrollWidth,document.body.scrollWidth))).toBeLessThanOrEqual(390);await expect.poll(()=>page.locator('svg.tl-svg').first().evaluate(el=>el.getBoundingClientRect().width)).toBeLessThan(390);await page.screenshot({path:'../.cache/screenshots/overview-refined-mobile.png',fullPage:true});expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
});

test('invalid data period clears previous totals while retaining freshly read sensor health',async({page})=>{
 await page.goto('/');await nav(page,'Energy');await expect(page.locator('.energy-chips')).toBeVisible();await page.getByRole('group',{name:'Period'}).getByRole('button',{name:'Custom',exact:true}).click();
 // One specific message for a future date, no Retry button, and the last good figures stay until a valid range is chosen.
 await page.getByLabel('Energy to date').fill('2099-01-02');await page.getByLabel('Energy from date').fill('2099-01-01');await expect(page.getByRole('alert')).toHaveText(/^That's in the future: today is \d{1,2} [A-Z][a-z]{2}\.$/);await expect(page.getByRole('button',{name:/Retry/})).toHaveCount(0);await expect(page.locator('.energy-chips')).toBeVisible();
 await expect(page.getByRole('heading',{name:'Sensors'})).toBeVisible();await expect(page.locator('.sensor-health')).toContainText('Demo readings');
});

test('sparse measured trends preserve isolated meter intervals without bridging gaps',async({page})=>{
 const now=Date.now();await page.route('**/api/telemetry/trends?*',r=>r.fulfill({json:{from:new Date(now-86400000).toISOString(),to:new Date(now).toISOString(),truncated:false,limit:1000,method:'Whole observed meter intervals',intervals:[1,null,2].map((value,n)=>({metric:'pv',start:new Date(now-(6-n)*3600000).toISOString(),end:new Date(now-(6-n)*3600000+1800000).toISOString(),averageKw:value,status:value===null?'gap':'observed',source:'Owned fixture',entityId:'sensor.pv'}))}}));await page.goto('/');const card=page.getByRole('article',{name:'Solar',exact:true});const line=card.locator('.metric-trend path.trend-line');await expect(line).toHaveCount(1);expect((await line.getAttribute('d'))!.match(/M/g)!.length).toBe(2);await expect(card.locator('.metric-trend svg')).toHaveAttribute('data-bridged-gaps','0');
});
test('daily measurement labels use the configured zone even in a UTC browser',async({browser,request})=>{
 const context=await browser.newContext({timezoneId:'UTC'}),page=await context.newPage();
 const st=await(await request.get('/api/telemetry/status')).json();st.timeZone='Europe/London';const day={from:'2026-10-01T23:00:00Z',to:'2026-10-02T23:00:00Z',metrics:{load:{energyKwh:1,coverageFraction:1},pv:{energyKwh:0,coverageFraction:1}},observedNetCostGbp:.3,costCoverageFraction:1};
 await page.route('**/api/telemetry/status',r=>r.fulfill({json:st}));await page.route('**/api/telemetry/summary?*',r=>r.fulfill({json:day}));await page.goto('/#/energy?from=2026-10-02&to=2026-10-02');const measured=page.locator('section').filter({has:page.getByRole('heading',{name:'Measured energy',exact:true})});const profile=measured.locator('figure.tl-day');await expect(profile).toContainText('Fri 2 Oct');await expect(profile).not.toContainText('Thu 1 Oct');await context.close();
});
