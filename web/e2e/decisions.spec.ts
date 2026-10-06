import { test, expect } from '@playwright/test';
import { openPage } from './support/navigation';
// Run this file on its own fresh isolated Demo fixture: the seeded recommendation is consumed.
test('denying a recommendation records the decision and leaves future permission off',async({page,request})=>{
 const initial=(await(await request.get('/api/state')).json()).state;
 const candidate=initial.proposals.find((p:{status:string})=>p.status==='Pending');
 expect(candidate,'A fresh isolated Demo fixture must contain its seeded pending proposal.').toBeTruthy();
 await request.post('/api/permissions/load_scaling',{data:{allowed:false}});
 await page.goto('/');await openPage(page, 'Suggestions');
 const card=page.locator('.recommendation').filter({hasText:candidate.title});await card.getByRole('button',{name:'Review',exact:true}).click();
 await page.getByRole('dialog').getByText("How it's checked").click();
 await expect(page.getByRole('dialog').getByRole('region',{name:'Expected impact preview'})).toBeVisible();
 await page.getByRole('dialog').getByRole('button',{name:'Decline…',exact:true}).click();
 // An optional reason in one tap, sent as the decline note.
 await page.getByRole('dialog').getByRole('group',{name:/Why are you declining/}).getByRole('button',{name:'We need that energy'}).click();
 await page.getByRole('dialog').getByRole('button',{name:'Decline',exact:true}).click();await expect(page.getByRole('dialog')).toBeHidden();
 await expect(page.getByRole('status').filter({hasText:"Declined. Joule won't suggest it again"})).toBeVisible();
 const current=(await(await request.get('/api/state')).json()).state;
 expect(current.proposals.find((p:{id:string})=>p.id===candidate.id).status).toBe('Denied');
 expect(current.proposals.find((p:{id:string})=>p.id===candidate.id).decisionNote).toBe('We need that energy');
 expect(current.settings.find((s:{key:string})=>s.key==='load_scaling').autoAllowed).toBe(false);
 expect(current.revision).toBe(initial.revision);
});
