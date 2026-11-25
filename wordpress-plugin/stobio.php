<?php
/**
 * Plugin Name:       Stobio
 * Description:       Connect your store to Stobio, the tracking reliability platform. Injects the official Stobio loader so your existing dataLayer events reach GA4 and Meta server-side.
 * Version:           1.0.1
 * Requires at least: 5.7
 * Requires PHP:      7.4
 * Author:            Stobio
 * Author URI:        https://stobio.com
 * License:           GPLv2 or later
 * License URI:       https://www.gnu.org/licenses/gpl-2.0.html
 * Text Domain:       stobio
 *
 * Stobio is a paid external service (14-day free trial). The plugin does nothing
 * until the site owner connects it with their Stobio site key — only then is the
 * loader script (served from the Stobio API origin) added to the site front end.
 */

if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

define( 'STOBIO_VERSION', '1.0.1' );
define( 'STOBIO_OPTION', 'stobio_settings' );
define( 'STOBIO_DEFAULT_LOADER_BASE', 'https://api.stobio.com' );

/**
 * Read settings merged over defaults.
 *
 * @return array{site_key:string,loader_base:string}
 */
function stobio_get_settings() {
	$defaults = array(
		'site_key'    => '',
		'loader_base' => STOBIO_DEFAULT_LOADER_BASE,
	);
	$stored = get_option( STOBIO_OPTION, array() );
	if ( ! is_array( $stored ) ) {
		$stored = array();
	}
	$settings = array_merge( $defaults, $stored );
	if ( '' === trim( (string) $settings['loader_base'] ) ) {
		$settings['loader_base'] = STOBIO_DEFAULT_LOADER_BASE;
	}
	return $settings;
}

/**
 * Sanitize submitted settings.
 *
 * Convenience: if the user pastes the full two-tag snippet from the Stobio panel
 * into the site-key field, the key (data-tenant) and loader origin are extracted
 * from it instead of rejecting the input.
 *
 * @param mixed $input Raw submitted value.
 * @return array Sanitized settings.
 */
function stobio_sanitize_settings( $input ) {
	$current = stobio_get_settings();
	if ( ! is_array( $input ) ) {
		return $current;
	}

	$raw_key  = isset( $input['site_key'] ) ? trim( (string) $input['site_key'] ) : '';
	$raw_base = isset( $input['loader_base'] ) ? trim( (string) $input['loader_base'] ) : '';

	// Full-snippet paste convenience: pull data-tenant (and loader origin) out of it.
	if ( '' !== $raw_key && false !== strpos( $raw_key, 'data-tenant' ) ) {
		if ( preg_match( '/data-tenant\s*=\s*["\']([^"\']+)["\']/i', $raw_key, $m ) ) {
			if ( '' === $raw_base && preg_match( '/src\s*=\s*["\'](https:\/\/[^"\']+)\/embed\/loader\.js["\']/i', $raw_key, $s ) ) {
				$raw_base = $s[1];
			}
			$raw_key = $m[1];
		}
	}

	$key = strtolower( $raw_key );
	if ( '' !== $key && ! preg_match( '/^[a-z0-9][a-z0-9-]{0,62}$/', $key ) ) {
		add_settings_error(
			STOBIO_OPTION,
			'stobio_invalid_key',
			__( 'The Stobio site key looks invalid. Copy the data-tenant value (or the whole snippet) from your Stobio panel and try again.', 'stobio' )
		);
		$key = $current['site_key'];
	}

	$base = STOBIO_DEFAULT_LOADER_BASE;
	if ( '' !== $raw_base ) {
		$candidate = untrailingslashit( esc_url_raw( $raw_base, array( 'https' ) ) );
		if ( '' !== $candidate ) {
			$base = $candidate;
		} else {
			add_settings_error(
				STOBIO_OPTION,
				'stobio_invalid_base',
				__( 'The loader URL must be an https:// address. The default Stobio address is used instead.', 'stobio' )
			);
		}
	}

	return array(
		'site_key'    => $key,
		'loader_base' => $base,
	);
}

/**
 * Print the official Stobio two-tag snippet in the front-end head.
 *
 * The first tag is the command stub: stobio('identify', ...) calls made before the
 * loader arrives queue up in .q and are drained on init. The second tag loads the
 * loader (defer) with the site key as data-tenant. Nothing is printed until the
 * site owner has entered a key (connecting the site is the documented consent step).
 */
function stobio_print_snippet() {
	$settings = stobio_get_settings();
	$key      = $settings['site_key'];
	if ( '' === $key ) {
		return;
	}

	/**
	 * Allow themes/plugins to suppress the Stobio snippet for a request.
	 *
	 * @param bool $should_inject Whether the snippet should be printed.
	 */
	if ( ! apply_filters( 'stobio_should_inject', true ) ) {
		return;
	}

	wp_print_inline_script_tag(
		'window.stobio=window.stobio||function(){(window.stobio.q=window.stobio.q||[]).push(arguments)};',
		array( 'id' => 'stobio-stub' )
	);
	wp_print_script_tag(
		array(
			'id'          => 'stobio-loader',
			'src'         => $settings['loader_base'] . '/embed/loader.js',
			'defer'       => true,
			'data-tenant' => $key,
		)
	);
}
add_action( 'wp_head', 'stobio_print_snippet', 20 );

/**
 * Register settings, section and fields.
 */
function stobio_admin_init() {
	register_setting(
		'stobio',
		STOBIO_OPTION,
		array(
			'type'              => 'array',
			'sanitize_callback' => 'stobio_sanitize_settings',
			'show_in_rest'      => false,
		)
	);

	add_settings_section(
		'stobio_connection',
		__( 'Connection', 'stobio' ),
		'stobio_render_section_intro',
		'stobio'
	);

	add_settings_field(
		'stobio_site_key',
		__( 'Stobio site key', 'stobio' ),
		'stobio_render_site_key_field',
		'stobio',
		'stobio_connection'
	);

	add_settings_field(
		'stobio_loader_base',
		__( 'Loader URL (advanced)', 'stobio' ),
		'stobio_render_loader_base_field',
		'stobio',
		'stobio_connection'
	);
}
add_action( 'admin_init', 'stobio_admin_init' );

/**
 * Settings section intro text.
 */
function stobio_render_section_intro() {
	printf(
		'<p>%s</p><p>%s</p>',
		esc_html__( 'Stobio is a tracking reliability platform: it receives your store’s events server-side and forwards them reliably to GA4 and Meta. Using it requires a Stobio account (paid service with a 14-day free trial).', 'stobio' ),
		wp_kses(
			sprintf(
				/* translators: 1: opening link tag to stobio.com, 2: closing link tag. */
				__( 'Find your site key in the %1$sStobio panel%2$s under Integrations — it is the data-tenant value in the tracking snippet. You can also paste the whole snippet below; the key is extracted automatically. Nothing is added to your site until a key is saved.', 'stobio' ),
				'<a href="https://stobio.com" target="_blank" rel="noopener noreferrer">',
				'</a>'
			),
			array( 'a' => array( 'href' => array(), 'target' => array(), 'rel' => array() ) )
		)
	);
}

/**
 * Site key field markup.
 */
function stobio_render_site_key_field() {
	$settings = stobio_get_settings();
	printf(
		'<input type="text" class="regular-text code" name="%1$s[site_key]" id="stobio_site_key" value="%2$s" placeholder="%3$s" autocomplete="off" />',
		esc_attr( STOBIO_OPTION ),
		esc_attr( $settings['site_key'] ),
		esc_attr__( 'e.g. mystore', 'stobio' )
	);
	printf(
		'<p class="description">%s</p>',
		esc_html__( 'The data-tenant value from your Stobio snippet, or paste the full snippet here.', 'stobio' )
	);
}

/**
 * Loader base URL field markup.
 */
function stobio_render_loader_base_field() {
	$settings = stobio_get_settings();
	printf(
		'<input type="url" class="regular-text code" name="%1$s[loader_base]" id="stobio_loader_base" value="%2$s" />',
		esc_attr( STOBIO_OPTION ),
		esc_attr( $settings['loader_base'] )
	);
	printf(
		'<p class="description">%s</p>',
		esc_html__( 'Leave as-is unless Stobio support tells you otherwise.', 'stobio' )
	);
}

/**
 * Register the options page.
 */
function stobio_admin_menu() {
	add_options_page(
		__( 'Stobio', 'stobio' ),
		__( 'Stobio', 'stobio' ),
		'manage_options',
		'stobio',
		'stobio_render_settings_page'
	);
}
add_action( 'admin_menu', 'stobio_admin_menu' );

/**
 * Render the settings page.
 */
function stobio_render_settings_page() {
	if ( ! current_user_can( 'manage_options' ) ) {
		return;
	}
	$settings  = stobio_get_settings();
	$connected = '' !== $settings['site_key'];
	?>
	<div class="wrap">
		<h1><?php esc_html_e( 'Stobio', 'stobio' ); ?></h1>
		<?php if ( $connected ) : ?>
			<p>
				<?php
				printf(
					/* translators: %s: the configured site key. */
					esc_html__( 'Connected — the Stobio snippet is live on your site front end for key “%s”.', 'stobio' ),
					esc_html( $settings['site_key'] )
				);
				?>
			</p>
		<?php endif; ?>
		<form action="options.php" method="post">
			<?php
			settings_fields( 'stobio' );
			do_settings_sections( 'stobio' );
			submit_button( $connected ? __( 'Save changes', 'stobio' ) : __( 'Connect', 'stobio' ) );
			?>
		</form>
	</div>
	<?php
}

/**
 * "Settings" quick link on the Plugins list row.
 *
 * @param string[] $links Existing action links.
 * @return string[]
 */
function stobio_plugin_action_links( $links ) {
	$settings_link = sprintf(
		'<a href="%s">%s</a>',
		esc_url( admin_url( 'options-general.php?page=stobio' ) ),
		esc_html__( 'Settings', 'stobio' )
	);
	array_unshift( $links, $settings_link );
	return $links;
}
add_filter( 'plugin_action_links_' . plugin_basename( __FILE__ ), 'stobio_plugin_action_links' );

/**
 * One-time nudge on the Plugins screen while the plugin is unconfigured.
 */
function stobio_admin_notice() {
	if ( ! current_user_can( 'manage_options' ) ) {
		return;
	}
	$screen = function_exists( 'get_current_screen' ) ? get_current_screen() : null;
	if ( ! $screen || 'plugins' !== $screen->id ) {
		return;
	}
	$settings = stobio_get_settings();
	if ( '' !== $settings['site_key'] ) {
		return;
	}
	printf(
		'<div class="notice notice-info"><p>%s</p></div>',
		wp_kses(
			sprintf(
				/* translators: 1: opening link tag to the settings page, 2: closing link tag. */
				__( 'Stobio is almost ready — %1$senter your site key%2$s to start measuring.', 'stobio' ),
				'<a href="' . esc_url( admin_url( 'options-general.php?page=stobio' ) ) . '">',
				'</a>'
			),
			array( 'a' => array( 'href' => array() ) )
		)
	);
}
add_action( 'admin_notices', 'stobio_admin_notice' );
