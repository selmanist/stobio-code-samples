<?php
/**
 * Stobio uninstall cleanup — removes the stored settings.
 */

if ( ! defined( 'WP_UNINSTALL_PLUGIN' ) ) {
	exit;
}

delete_option( 'stobio_settings' );
