/* Prints sizeof/offsetof for every rcheevos struct the C# interop marshals.
 * RcheevosInterop.VerifyAbi() asserts the same values at runtime; build.sh
 * stores the output in rcheevos-abi.txt for eyeballing on version bumps. */
#include <stdio.h>
#include <stddef.h>
#include "rc_client.h"
#include "rc_api_request.h"

#define P(s, f) printf("%s.%s=%zu\n", #s, #f, offsetof(s, f))
#define S(s)    printf("sizeof(%s)=%zu\n", #s, sizeof(s))

int main(void)
{
    S(rc_client_event_t);
    P(rc_client_event_t, type);
    P(rc_client_event_t, achievement);
    P(rc_client_event_t, server_error);

    S(rc_client_achievement_t);
    P(rc_client_achievement_t, title);
    P(rc_client_achievement_t, badge_name);
    P(rc_client_achievement_t, measured_progress);
    P(rc_client_achievement_t, measured_percent);
    P(rc_client_achievement_t, unlock_time);
    P(rc_client_achievement_t, state);
    P(rc_client_achievement_t, rarity);
    P(rc_client_achievement_t, type);

    S(rc_client_user_t);
    P(rc_client_user_t, token);
    P(rc_client_user_t, score);

    S(rc_client_game_t);
    P(rc_client_game_t, title);
    P(rc_client_game_t, badge_name);

    S(rc_client_leaderboard_t);
    P(rc_client_leaderboard_t, tracker_value);
    P(rc_client_leaderboard_t, lower_is_better);

    S(rc_client_leaderboard_scoreboard_t);
    P(rc_client_leaderboard_scoreboard_t, submitted_score);
    P(rc_client_leaderboard_scoreboard_t, new_rank);

    S(rc_api_server_response_t);
    P(rc_api_server_response_t, body_length);
    P(rc_api_server_response_t, http_status_code);

    printf("RC_CLIENT_LEADERBOARD_DISPLAY_SIZE=%d\n", RC_CLIENT_LEADERBOARD_DISPLAY_SIZE);
    return 0;
}
