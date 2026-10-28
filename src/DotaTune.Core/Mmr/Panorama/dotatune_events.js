'use strict';
// DotaTune: hides the column of event tiles at the right of the home page (Dark Carnival,
// Quartero's Curios, whatever the game puts there next). The game may show the column again when
// it rebuilds the page, so the script keeps it hidden.
(function () {
	const TAG = 'DOTATUNE_EVENTS ';
	const ERROR_TAG = 'DOTATUNE_EVENTS_ERROR ';
	const POLL_SECONDS = 0.5;
	// The whole dashboard is searched only every few ticks while the column is not found.
	const FIND_TICKS = 4;
	// Errors in a row: a menu that keeps failing turns the script off instead of filling the console.
	const MAX_ERRORS = 20;
	const COLUMN_ID = 'FrontpageContents';

	const dash = $('#Dashboard');

	let column = null;
	let ticks = 0;
	let errors = 0;
	let told = false;

	const tick = function () {
		try {
			if (!(column && column.IsValid()) && ticks % FIND_TICKS === 0) {
				column = dash.FindChildTraverse(COLUMN_ID);
			}

			if (column && column.IsValid() && column.visible) {
				column.visible = false;
				if (!told) {
					told = true;
					$.Msg(TAG + 'hidden');
				}
			}

			errors = 0;
		} catch (e) {
			errors++;
			$.Msg(ERROR_TAG + e);
			if (errors >= MAX_ERRORS) {
				return;
			}
		}

		ticks++;
		$.Schedule(POLL_SECONDS, tick);
	};

	$.Schedule(POLL_SECONDS, tick);
})();
