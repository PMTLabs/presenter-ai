# Background context — Ricoh Project Delivery Overview (Q4 2026)

How to answer questions: you are speaking for the FPT delivery team to Ricoh stakeholders. Use the facts below. Be concise and concrete (numbers, names, cadences). If something is not covered here, say it is not in this deck and offer to follow up.

## Engagement overview
- 5 products supported. Development: CPE (Content Parsing Engine), DCD (Dynamic Cloud Database). Maintenance: VPD (Virtual Printer Driver), REDE (RICOH Electronic Data Exchange), RSI/Boomi (Ricoh Smart Integration).
- Team size: 10. Shared delivery model: CPE, DCD, VPD and REDE are covered by a shared pool of 9 members; RSI/Boomi has one primary member.
- Ricoh stakeholders: CPE and DCD = Jared, Niloofar; VPD = Teyu; REDE = Phuc Nguyen; RSI/Boomi = Teyu, Yuwen.

## Team structure
- 10 resources. Shared delivery across multiple products. REDE coverage comes from shared DCD capacity. RSI/Boomi: one primary engineer. DevOps: shared coverage.

## Offshore resource plan (headcount)
- Actual: Jan 17, Feb 17, Mar 17, Apr 11, May 11, Jun 11, Jul 10.5, Aug 10, Sep 10.
- Plan: Oct 10, Nov 10, Dec 10.
- Trend: 17 → 11 → 10. Stable at 10 since August 2026; same level planned through Q4 2026.
- Q4 allocation: CPE = Do Luong, Tham Bui, Thoan Vo, Hien Le, plus shared management and DevOps. DCD = Dong Huynh, Huy Le, 50% Thoa Phung, plus shared DevOps. REDE = 50% Thoa Phung. RSI/Boomi = 100% Tien Nguyen. Cross-product = Quyen Le, Huy Nguyen.
- Product totals must not be summed as separate headcounts because several people are shared.

## Governance
- Planning: backlog and tickets in Azure DevOps (ADO); weekly priority and work planning; 3-week sprint planning for CPE/DCD; continuous intake for maintenance.
- Monitoring: internal daily meeting; ADO board and ticket review; weekly meeting with Ricoh; blocker and priority review.
- Reporting: weekly status report by email, sent Monday Vietnam time (Sunday US), to Jared and Niloofar; covers done, work in progress, blockers, next plan.
- Rhythm: daily (internal progress, blockers); weekly (Ricoh status and priority alignment); every 3 weeks (CPE/DCD backlog review and sprint planning); after each sprint (retrospective: lessons and improvement actions).
- Decision flow: Ricoh stakeholders → FPT PM / Solution Architect → product members → QA / DevOps.

## Current model (today)
- Flow: CPE/DCD backlog item or ad-hoc request in ADO (ad-hoc arrives by end-user or dealer email) → clarification, estimation, assignment → development/fix and FPT testing → release and closure.
- CPE/DCD run against a defined roadmap and backlog; ad-hoc requests layer on top with volume varying week to week; both compete for the same capacity; no consistent Story Point or velocity baseline yet.
- Why change: formalize planning predictability for CPE/DCD; make ad-hoc/support consumption visible; protect planned backlog from unpredictable ad-hoc volume.
- Strength: high responsiveness. Limitation: ad-hoc volume can crowd out planned backlog work.

## Proposed hybrid delivery model
- CPE/DCD development: fixed three-week sprint; Story Point estimation; prioritized sprint backlog; development and FPT functional testing; sprint review and outcome measurement. Flow: Backlog → SP estimate → 3-week sprint → Review/Release.
- VPD/REDE/RSI-Boomi maintenance and support: continuous ticket flow; continuous intake and clarification; priority by impact and readiness; implementation or issue resolution; measured by throughput and cycle time. Flow: Intake → Prioritize → Implement/Fix → Close.
- Completed work may be released earlier where appropriate; the three-week sprint cadence stays fixed.

## Outcome-based capacity for CPE and DCD (from October 2026)
- Sprint length 3 weeks, fixed. Estimation unit: Story Point. Scope: CPE and DCD only. Commitment: completed outcomes. Historical velocity: not yet available.
- Capacity split: 70% planned backlog, 30% hot support.
- Baseline establishment: Sprint 1 start SP estimation and record delivery/support; Sprint 2 review consistency and actual support usage; Sprint 3 calibrate estimates and review the 70/30 split; Sprint 4+ use rolling average for forecasting.
- If the 30% reserve is not consumed, pull additional backlog tickets by Ricoh-confirmed priority. The sprint does not end early.

## Execution flow and decision responsibilities
- Ricoh: business priority. FPT PM / SA: impact, sequence, capacity. Delivery team: Story Point estimates and dependencies. Sprint planning: final CPE/DCD content.
- Tickets move from backlog through sprint stages into production (ticket types: defect, improvement, feature). Maintenance products continue through continuous ticket flow.

## Roadmap
- CPE/DCD roadmap detail is in the separate "CPE & DCD Product Roadmap" presentation.

## Alignment topics for discussion
- Hybrid model; 70/30 capacity split; prioritization cadence; Q4 resource plan.
