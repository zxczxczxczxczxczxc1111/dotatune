'use strict';
// DotaTune: MMR and its change since the previous reading, as a line in the player card of the
// menu and next to the MMR number in the profile. The number is read only from the profile page
// the player opened. Every new reading goes to the console log, DotaTune keeps them and hands the
// last one of each account back in DotaTuneMmrData.
(function () {
	const TAG = 'DOTATUNE_MMR ';
	const ERROR_TAG = 'DOTATUNE_MMR_ERROR ';
	const POLL_SECONDS = 1.0;
	const FIND_TICKS = 2;
	const RESCAN_TICKS = 10;
	// Errors in a row: a page that keeps failing turns the line off instead of filling the console.
	const MAX_ERRORS = 20;
	const ROWS_WAIT_TICKS = 5;
	const UP = '#74b14a';
	const DOWN = '#d84535';
	const PAGE_TYPE = 'DOTAProfileHeroStatsPage';

	const data = typeof DotaTuneMmrData === 'object' && DotaTuneMmrData !== null ? DotaTuneMmrData : {};
	const known = typeof data.accounts === 'object' && data.accounts !== null ? data.accounts : {};
	const russian = data.lang === 'ru' || (data.lang === undefined && $.Language() === 'russian');
	const dash = $('#Dashboard');

	let account = null;
	let last = null;
	let calibrated = true;
	let card = null;
	let menuLabel = null;
	let pages = [];
	let ticks = 0;
	let errors = 0;
	// The newest ranked game of the list the profile showed with the number of the last reading.
	let seenTop = null;
	// The number and the newest game the profile has shown for `waited` ticks; closing it does not reset them.
	let waitKey = null;
	let waited = 0;

	const isReading = function (r) {
		return typeof r === 'object' && r !== null && Number.isInteger(r.mmr) && r.mmr > 0;
	};

	const signed = function (n) {
		return n > 0 ? '+' + n : String(n);
	};

	const gamesText = function (games) {
		if (!russian) {
			return ' over ' + games + ' games';
		}

		// "za N igru / igry / igr"
		const mod10 = games % 10;
		const mod100 = games % 100;
		const word = mod10 === 1 && mod100 !== 11 ? 'игру'
			: mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14) ? 'игры' : 'игр';
		return ' за ' + games + ' ' + word;
	};

	const change = function (reading, withGames) {
		if (!Number.isInteger(reading.delta)) {
			return '';
		}

		const color = reading.delta > 0 ? UP : reading.delta < 0 ? DOWN : '#c0c0c0';
		const games = withGames && Number.isInteger(reading.games) && reading.games > 1 ? gamesText(reading.games) : '';
		return ' <font color="' + color + '">(' + signed(reading.delta) + games + ')</font>';
	};

	const findCard = function () {
		if (card === null || !card.IsValid()) {
			card = dash.FindChildTraverse('MiniShowcase');
		}

		return card;
	};

	const findAccount = function () {
		const found = findCard();
		const id = found ? Number(found.accountid) : NaN;
		return Number.isInteger(id) && id > 0 ? String(id) : null;
	};

	// The third line under the name and the status in the card at the top right of the menu.
	const showInMenu = function () {
		const show = calibrated && isReading(last);
		if (menuLabel === null || !menuLabel.IsValid()) {
			if (!show) {
				return;
			}

			const found = findCard();
			const name = found ? found.FindChildTraverse('UserName') : null;
			const details = name ? name.GetParent() : null;
			if (details === null) {
				return;
			}

			menuLabel = $.CreatePanel('Label', details, 'DotaTuneMmr');
			menuLabel.html = true;
			menuLabel.hittest = false;
			menuLabel.style.fontSize = '17px';
			menuLabel.style.color = '#dcd087';
			menuLabel.style.textShadow = '0px 1px 4px black';
		}

		menuLabel.visible = show;
		if (show) {
			menuLabel.text = 'MMR ' + last.mmr + change(last, true);
		}
	};

	// Next to the number in the MMR block of the profile; the other labels of that block are collapsed.
	const showInPage = function (page, reading) {
		const number = page.FindChildTraverse('MMRNumber');
		const box = number ? number.GetParent() : null;
		if (box === null) {
			return;
		}

		let label = box.FindChild('DotaTuneMmrChange');
		const text = reading === null ? '' : change(reading, false);
		if (label === null) {
			if (text === '') {
				return;
			}

			box.style.flowChildren = 'right';
			label = $.CreatePanel('Label', box, 'DotaTuneMmrChange');
			label.html = true;
			label.hittest = false;
			label.AddClass('MMRValue');
			label.style.marginLeft = '6px';
		}

		label.visible = text !== '';
		label.text = text;
	};

	const textOf = function (row, cls) {
		const found = row.FindChildrenWithClassTraverse(cls);
		return found.length > 0 ? found[0].text : '';
	};

	// Ranked rows of the match history from the newest, each as hero, date, time and duration.
	const rankedRows = function (page) {
		const table = page.FindChildTraverse('RecentGamesTable');
		if (table === null) {
			return null;
		}

		const ranked = [$.Localize('#dota_lobby_type_competitive'), $.Localize('#DOTA_lobby_type_name_ranked')];
		const rows = [];
		for (let i = 0; i < table.GetChildCount(); i++) {
			const row = table.GetChild(i);
			if (row.BHasClass('RecentGame') && ranked.indexOf(textOf(row, 'GameTypeColumn')) >= 0) {
				rows.push([textOf(row, 'HeroName'), textOf(row, 'TimestampDate'), textOf(row, 'TimestampTime'), textOf(row, 'DurationColumn')].join('|'));
			}
		}

		return rows;
	};

	const readNumber = function (page) {
		const digits = $.Localize('#ranked_mmr_value', page).replace(/\D+/g, '');
		const mmr = digits.length > 0 && digits.length <= 5 ? parseInt(digits, 10) : NaN;
		return mmr > 0 ? mmr : null;
	};

	// The page and every panel above it are shown: a profile closed earlier is not read.
	const shown = function (panel) {
		for (let p = panel; p; p = p.GetParent()) {
			if (!p.visible) {
				return false;
			}
		}

		return true;
	};

	const take = function (page) {
		if (!page.BHasClass('LocalUser') || !shown(page)) {
			return;
		}

		if (!page.BHasClass('MMRCalibrated')) {
			// Uncalibrated: the game hides the number and keeps none to read.
			if (page.BHasClass('MMRCalibrating') || page.BHasClass('MMRNoData')) {
				calibrated = false;
			}

			showInPage(page, null);
			return;
		}

		const mmr = readNumber(page);
		if (mmr === null) {
			return;
		}

		calibrated = true;
		const rows = rankedRows(page);
		const newest = rows !== null && rows.length > 0 ? rows[0] : null;
		const hasTop = isReading(last) && typeof last.top === 'string';
		if (isReading(last) && last.mmr === mmr) {
			const after = hasTop && newest !== null ? rows.indexOf(last.top) : -1;
			if (after > 1 && steady(mmr, newest)) {
				// Ranked games above the newest one of the reading with the number where it was, a loss and a
				// win: they get a reading of their own, or the next change would be put on them too.
				record(mmr, after, newest);
			} else if (after === 1) {
				// A scored ranked game always moves the number, so this one is in it or did not count: the reading kept a
				// list without it. Should the number be late instead, the next reading waits and has no count.
				last = { mmr: last.mmr, delta: last.delta, games: last.games, top: newest, at: last.at };
			}

			if (newest !== null) {
				seenTop = newest;
			}

			showInPage(page, last);
			return;
		}

		// The profile shows the list it had and asks for a new one, so after a game the number is new
		// before the list is. A list with the newest game of the reading, or of the list seen with the old
		// number, leaves the new game out: a reading taken from it would count wrong, so it waits and then
		// keeps no list at all.
		const stale = newest === null || (hasTop && newest === last.top) || newest === seenTop;
		if (stale && !steady(mmr, newest)) {
			return;
		}

		let games = null;
		if (!stale && hasTop) {
			const at = rows.indexOf(last.top);
			games = at >= 0 ? at : null;
		}

		seenTop = newest;
		record(mmr, games, stale ? null : newest);
		showInPage(page, last);
	};

	// Data the profile has just been given can still change: true once it has shown the same number
	// and newest game for ROWS_WAIT_TICKS ticks.
	const steady = function (mmr, newest) {
		const key = mmr + '|' + newest;
		if (key !== waitKey) {
			waitKey = key;
			waited = 0;
		}

		waited++;
		return waited > ROWS_WAIT_TICKS;
	};

	const record = function (mmr, games, top) {
		last = {
			mmr: mmr,
			delta: isReading(last) ? mmr - last.mmr : null,
			games: games,
			top: top,
			at: Math.floor(Date.now() / 1000),
		};
		$.Msg(TAG + JSON.stringify({ v: 1, account: Number(account), mmr: last.mmr, delta: last.delta, games: last.games, top: last.top, at: last.at }));
	};

	const findPages = function () {
		const found = [];
		dash.FindChildrenWithClassTraverse('MMRNumberContainer').forEach(function (box) {
			let p = box;
			while (p && p.paneltype !== PAGE_TYPE) {
				p = p.GetParent();
			}

			if (p && found.indexOf(p) < 0) {
				found.push(p);
			}
		});
		return found;
	};

	const tick = function () {
		try {
			if (account === null) {
				account = findAccount();
				if (account !== null && isReading(known[account])) {
					last = known[account];
				}
			}

			if (account !== null) {
				pages = pages.filter(function (p) { return p.IsValid(); });
				// The whole dashboard is searched: every few seconds until the profile is first opened, then less often.
				if (ticks % (pages.length === 0 ? FIND_TICKS : RESCAN_TICKS) === 0) {
					pages = findPages();
				}

				pages.forEach(take);
				showInMenu();
			}

			errors = 0;
		} catch (e) {
			errors++;
			$.Msg(ERROR_TAG + e);
			if (errors >= MAX_ERRORS) {
				if (menuLabel !== null && menuLabel.IsValid()) {
					menuLabel.visible = false;
				}

				return;
			}
		}

		ticks++;
		$.Schedule(POLL_SECONDS, tick);
	};

	$.Schedule(POLL_SECONDS, tick);
})();
