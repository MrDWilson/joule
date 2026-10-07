import { test, expect, type Page } from '@playwright/test';
import { openPage } from './support/navigation';
const nav = openPage;

test('retry measurements refreshes telemetry immediately without waiting for a state change',async({page})=>{
 let calls=0, fail=true;
 await page.route('**/api/telemetry/status',async route=>{calls++;if(fail)await route.fulfill({status:503,json:{error:'Owned temporary telemetry outage'}});else await route.continue();});
 await page.goto('/');await expect(page.getByRole('alert')).toContainText('Owned temporary telemetry outage');
 const before=calls;fail=false;await page.getByRole('alert').filter({hasText:'Couldn’t load today’s readings'}).getByRole('button',{name:'Try again',exact:true}).click();
 await expect.poll(()=>calls,{timeout:3000}).toBeGreaterThan(before);
 await expect(page.getByRole('alert').filter({hasText:'Owned temporary telemetry outage'})).toHaveCount(0);
});

test('historical plan measurements and evidence refresh after a collection while keeping the selection',async({page,request})=>{
 const payload=await(await request.get('/api/state')).json();let generation=0;const selected={...payload.plan,id:'owned-refresh-plan',slots:[{...payload.plan.slots[0],time:new Date(Date.now()-3600000).toISOString(),durationMinutes:30,loadActual:null}]};
 await page.route('**/api/state',r=>r.fulfill({json:{...payload,state:{...payload.state,lastCollection:new Date(Date.now()+generation*60000).toISOString()}}}));
 await page.route('**/api/plans?*',r=>r.fulfill({json:{items:[{id:selected.id,at:selected.at,source:'Owned fixture'}],total:1,offset:0,limit:50}}));
 await page.route('**/api/plans/owned-refresh-plan',r=>r.fulfill({json:{...selected,slots:selected.slots.map((s:any)=>({...s,loadActual:generation?2.34:null}))}}));
 await page.route('**/api/telemetry/plans/owned-refresh-plan/evidence',r=>r.fulfill({json:{planId:selected.id,slots:selected.slots.map((s:any)=>({...s,actual:{metrics:{load:{energyKwh:generation?2.34:null,coverageFraction:generation?1:0}}},estimatedLoadKwh:null,estimatedPvKwh:null,estimateMethod:'Owned fixture'}))}}));
 // The comparison panel only renders for a configured source, so the fixture reports one so its reason text is visible.
 await page.route('**/api/telemetry/plans/owned-refresh-plan/alternative',r=>r.fulfill({json:{available:true,reason:generation?'Owned newer alternative evidence':'Owned older alternative evidence',methodology:'Fixture',entityId:'sensor.owned_alternative',source:'Owned fixture',capturedAt:new Date().toISOString(),matchedSlots:0,predbatMaeKwhPerHalfHour:null,alternativeMaeKwhPerHalfHour:null,slots:[]}}));
 await page.clock.install();await page.goto('/');await nav(page,'Plan');await page.getByText('Browse earlier plans',{exact:true}).click();await page.getByLabel('Plan snapshot',{exact:true}).selectOption(selected.id);
 await expect(page.getByText('Owned older alternative evidence')).toBeVisible();
 generation=1;await page.clock.fastForward(11000);
 await expect(page.getByLabel('Plan snapshot',{exact:true})).toHaveValue(selected.id);
 await expect(page.getByRole('status').filter({hasText:'Showing the plan Predbat made'})).toBeVisible();
 const evidence=page.locator('section').filter({has:page.getByRole('heading',{name:'How this plan’s forecast did',exact:true})});
 await evidence.locator('summary').filter({ hasText: /finished half-hour/ }).click();
 await expect(evidence).toContainText('2.34 kWh');await expect(page.getByText('Owned newer alternative evidence')).toBeVisible();
});

test('plan date filters and interval labels use the configured timezone in a UTC browser',async({browser,request})=>{
 const context=await browser.newContext({timezoneId:'UTC'}),page=await context.newPage();
 try{
 const st=await(await request.get('/api/telemetry/status')).json();st.timeZone='Europe/London';
 const payload=await(await request.get('/api/state')).json();payload.plan.slots=[{...payload.plan.slots[0],time:'2027-10-01T23:00:00Z',durationMinutes:30,action:'Charge'}];
 await page.route('**/api/telemetry/status',r=>r.fulfill({json:st}));await page.route('**/api/state',r=>r.fulfill({json:payload}));
 await page.goto('/');await expect(page.getByText(/^Today counts from midnight/)).toContainText('(Europe/London time)');await nav(page,'Plan');await page.getByText('Browse earlier plans',{exact:true}).click();
 const call=page.waitForRequest(r=>r.url().includes('/api/plans?')&&r.url().includes('from='));await page.getByLabel('Snapshot generation date').fill('2026-10-02');const url=new URL((await call).url());
 expect(url.searchParams.get('from')).toBe('2026-10-01T23:00:00.000Z');expect(url.searchParams.get('to')).toBe('2026-10-02T23:00:00.000Z');
 // 23:00 UTC on 1 Oct 2027 is 00:00 on Sat 2 Oct in London: the day header and the time are London's, not the browser's.
 const next=page.getByRole('region',{name:"What's next, window by window"});await expect(next.getByRole('rowheader',{name:'Sat 2 Oct 2027',exact:true})).toBeVisible();await expect(next.getByRole('rowheader',{name:/00:00–00:30/})).toBeVisible();
 }finally{await context.close();}
});

test('Today shows the next 12 hours of windows and the next 48 on request',async({page,request})=>{
 const payload=await(await request.get('/api/state')).json();const start=Math.ceil(Date.now()/1800000)*1800000;payload.plan.slots=Array.from({length:48},(_,n)=>({...payload.plan.slots[0],actionKey:null,actionId:null,actionLabel:null,rawAction:null,targetPercent:null,time:new Date(start+n*1800000).toISOString(),durationMinutes:30,action:Math.floor(n/4)%2?'Charge':'Demand'}));
 await page.route('**/api/state',r=>r.fulfill({json:payload}));await page.goto('/');const windows=page.getByRole('region',{name:'Coming up'});
 await expect(windows.locator('.window-item')).toHaveCount(6);await windows.getByRole('button',{name:'Show the next 48 hours'}).click();await expect(windows.locator('.window-item')).toHaveCount(12);await windows.getByRole('button',{name:'Show the next 12 hours'}).click();await expect(windows.locator('.window-item')).toHaveCount(6);
});

test('an older delayed poll cannot overwrite a newer workspace revision',async({page,request})=>{
 // Polls never overlap, so a newer response can only come from your own action (here Refresh now) while a poll is held.
 const payload=await(await request.get('/api/state')).json();let holdNext=false,held:any=null,release:()=>void=()=>{};
 await page.route('**/api/collect',r=>r.fulfill({status:204}));await page.route('**/api/telemetry/collect',r=>r.fulfill({status:204}));
 await page.route('**/api/state',async route=>{const copy=structuredClone(payload);if(holdNext){holdNext=false;held={route,copy};await new Promise<void>(resolve=>{release=resolve;});}else await route.fulfill({json:copy});});
 await page.clock.install();await page.goto('/');await nav(page,'Settings');
 holdNext=true;await page.clock.fastForward(11000);await expect.poll(()=>!!held).toBe(true);
 payload.state.revision=100;await page.getByRole('button',{name:'Refresh now'}).click();await expect(page.getByText('Settings version 100',{exact:true})).toBeVisible();
 const oldResponse=page.waitForResponse(r=>r.url().endsWith('/api/state'));await held.route.fulfill({json:held.copy});release();await(await oldResponse).finished();await page.clock.runFor(50);
 await expect(page.getByText('Settings version 100',{exact:true})).toBeVisible();
});

test('failed provider runs without usage do not claim zero token consumption',async({page,request})=>{
 const payload=await(await request.get('/api/state')).json();payload.state.usage=[{at:'2026-10-02T10:00:00Z',provider:'ChatGpt',model:'owned-missing-usage',inputTokens:0,outputTokens:0,estimatedUsd:null,status:'Failed'}];
 await page.route('**/api/state',r=>r.fulfill({json:payload}));await page.goto('/');await nav(page,'AI checks');
 const row=page.locator('.usage-row').filter({hasText:'owned-missing-usage'});await expect(row).toContainText('Tokens not reported');await expect(row).not.toContainText(/\b0 in\b/);await expect(page.getByText(/Checks that didn't finish may still use some of your allowance/)).toBeVisible();
});

test('measured summaries are fetched one at a time and the newest reading wins',async({page,request})=>{
 const payload=await(await request.get('/api/state')).json();let holdNext=false,held:any=null,release:()=>void=()=>{},cost=1.23,calls=0;
 await page.route('**/api/state',r=>r.fulfill({json:payload}));await page.route('**/api/collect',r=>r.fulfill({status:204}));await page.route('**/api/telemetry/collect',r=>r.fulfill({status:204}));
 await page.route('**/api/telemetry/summary?*',async route=>{
  // Count the 'today so far' poll only (Today also reads last night's window and the whole of yesterday, once).
  const to=Date.parse(new URL(route.request().url()).searchParams.get('to')!);const soFar=[0,86400000].some(back=>Math.abs(Date.now()-back-to)<3600000);if(soFar)calls++;const result={from:new Date(Date.now()-3600000).toISOString(),to:new Date().toISOString(),observedNetCostGbp:cost,costCoverageFraction:1,metrics:{},limitations:[],sources:[]};if(holdNext&&soFar){holdNext=false;held={route,result};await new Promise<void>(resolve=>{release=resolve;});}else await route.fulfill({json:result});});
 await page.clock.install();await page.goto('/');await expect(page.locator('.stat-value').filter({hasText:'£1.23'})).toBeVisible();
 holdNext=true;await page.getByRole('button',{name:'Refresh now'}).click();await expect.poll(()=>!!held).toBe(true);
 // While that request is held nothing else is sent, however long it takes.
 const during=calls;await page.clock.fastForward(11000);await page.clock.runFor(50);expect(calls).toBe(during);
 cost=8.88;await held.route.fulfill({json:held.result});release();
 await page.getByRole('button',{name:'Refresh now'}).click();await expect(page.locator('.stat-value').filter({hasText:'£8.88'})).toBeVisible();
});

test('a temporary telemetry failure preserves the known configured timezone',async({page,request})=>{
 const st=await(await request.get('/api/telemetry/status')).json();st.timeZone='Pacific/Auckland';let fail=false;
 await page.route('**/api/telemetry/status',r=>fail?r.fulfill({status:503,json:{error:'Owned timezone outage'}}):r.fulfill({json:st}));
 await page.clock.install();await page.goto('/');await expect(page.getByText(/Predbat plans next \(Pacific\/Auckland time\)$/)).toBeVisible();
 // Telemetry status is re-read on a new collection or a manual refresh, not on a fixed timer, so ask for it explicitly.
 fail=true;await page.clock.fastForward(11000);await page.getByRole('button',{name:'Refresh now'}).click();await expect(page.getByRole('alert')).toContainText('Owned timezone outage');await expect(page.getByText(/Predbat plans next \(Pacific\/Auckland time\)$/)).toBeVisible();
});

test('an older authentication challenge cannot hide a newer valid workspace',async({page,request})=>{
 const payload=await(await request.get('/api/state')).json();let holdNext=false,held:any=null,release:()=>void=()=>{};
 await page.route('**/api/collect',r=>r.fulfill({status:204}));await page.route('**/api/telemetry/collect',r=>r.fulfill({status:204}));
 await page.route('**/api/state',async route=>{if(holdNext){holdNext=false;held=route;await new Promise<void>(resolve=>{release=resolve;});}else await route.fulfill({json:payload});});
 await page.clock.install();await page.goto('/');await nav(page,'Settings');
 holdNext=true;await page.clock.fastForward(11000);await expect.poll(()=>!!held).toBe(true);
 payload.state.revision=100;await page.getByRole('button',{name:'Refresh now'}).click();await expect(page.getByText('Settings version 100',{exact:true})).toBeVisible();
 const oldResponse=page.waitForResponse(r=>r.url().endsWith('/api/state'));await held.fulfill({status:401,json:{authMode:'AccessKey',error:'Owned stale challenge'}});release();await(await oldResponse).finished();await page.clock.runFor(50);
 await expect(page.getByText('Settings version 100',{exact:true})).toBeVisible();await expect(page.getByLabel('Server access key')).toHaveCount(0);
});
