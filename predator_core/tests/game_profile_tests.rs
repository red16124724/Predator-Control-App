use predator_core::*;
use std::sync::Arc;
use std::thread;

static PROFILE_TEST_MUTEX: std::sync::Mutex<()> = std::sync::Mutex::new(());

#[test]
fn test_game_profile_c_abi_lifecycle_and_validation() {
    let _lock = PROFILE_TEST_MUTEX.lock().unwrap_or_else(|e| e.into_inner());

    unsafe {
        // 1. Initialize subsystem
        let init_res = predator_game_profile_init();
        assert_eq!(init_res, 0, "Init must return 0");

        // Clear any leftover state
        assert_eq!(predator_game_profile_clear(), 0);

        // 2. Active query on empty engine
        let mut active_prof = 999u32;
        let mut active_pid = 999u32;
        assert_eq!(
            predator_game_profile_get_active(&mut active_prof, &mut active_pid),
            0,
            "Get active must return 0 when no game is active"
        );
        assert_eq!(active_prof, 0);
        assert_eq!(active_pid, 0);

        // 3. Register valid profile
        let exe = b"witcher3.exe\0";
        let path = b"D:\\Games\\Witcher 3\\witcher3.exe\0";
        let reg_res = predator_game_profile_register(
            1,
            exe.as_ptr(),
            path.as_ptr(),
            2,
            55,
            115,
            1,
            144,
            1,
        );
        assert_eq!(reg_res, 0, "Valid registration must return 0");

        // 4. Register profile without full path (null pointer allowed for full_path)
        let exe2 = b"cs2.exe\0";
        let reg_res2 = predator_game_profile_register(
            2,
            exe2.as_ptr(),
            std::ptr::null(),
            3,
            75,
            135,
            2,
            240,
            2,
        );
        assert_eq!(reg_res2, 0, "Registration with null full_path must return 0");

        // 5. Invalid parameters
        // ID 0
        assert_eq!(
            predator_game_profile_register(
                0,
                exe.as_ptr(),
                path.as_ptr(),
                1,
                50,
                100,
                0,
                60,
                0
            ),
            -1,
            "Profile ID 0 must be rejected"
        );

        // Null executable name
        assert_eq!(
            predator_game_profile_register(
                3,
                std::ptr::null(),
                std::ptr::null(),
                1,
                50,
                100,
                0,
                60,
                0
            ),
            -1,
            "Null exe_name must be rejected"
        );

        // Blocked executable
        let blocked = b"explorer.exe\0";
        assert_eq!(
            predator_game_profile_register(
                4,
                blocked.as_ptr(),
                std::ptr::null(),
                1,
                50,
                100,
                0,
                60,
                0
            ),
            -2,
            "Blocked executable must be rejected"
        );

        // 6. Unregister
        assert_eq!(
            predator_game_profile_unregister(1),
            0,
            "Unregister existing profile must return 0"
        );
        assert_eq!(
            predator_game_profile_unregister(999),
            -1,
            "Unregister non-existent profile must return -1"
        );

        // 7. Clear
        assert_eq!(predator_game_profile_clear(), 0);

        // 8. Shutdown
        assert_eq!(predator_game_profile_shutdown(), 0);
    }
}

#[test]
fn test_game_profile_c_abi_event_polling_and_serialization() {
    let _lock = PROFILE_TEST_MUTEX.lock().unwrap_or_else(|e| e.into_inner());

    unsafe {
        assert_eq!(predator_game_profile_init(), 0);
        assert_eq!(predator_game_profile_clear(), 0);

        // Register profiles
        let exe1 = b"doom.exe\0";
        assert_eq!(
            predator_game_profile_register(10, exe1.as_ptr(), std::ptr::null(), 2, 65, 125, 1, 144, 1),
            0
        );

        let exe2 = b"eldenring.exe\0";
        assert_eq!(
            predator_game_profile_register(20, exe2.as_ptr(), std::ptr::null(), 3, 85, 145, 2, 60, 2),
            0
        );

        // Poll empty queue
        let mut ev_type = 99i32;
        let mut prof_id = 99u32;
        let mut pid = 99u32;
        assert_eq!(
            predator_game_profile_poll_event(&mut ev_type, &mut prof_id, &mut pid),
            0,
            "Poll empty queue must return 0"
        );
        assert_eq!(ev_type, 0);
        assert_eq!(prof_id, 0);
        assert_eq!(pid, 0);

        // Null pointer safety for poll
        assert_eq!(
            predator_game_profile_poll_event(std::ptr::null_mut(), &mut prof_id, &mut pid),
            -1,
            "Null out pointer must return -1"
        );

        // Simulate GameStarted (Doom pid 5555)
        game_profile::with_engine(|engine| {
            engine.handle_foreground_transition(5555, "doom.exe", "", 10_000);
        });

        // Verify active game
        assert_eq!(
            predator_game_profile_get_active(&mut prof_id, &mut pid),
            1,
            "Get active must return 1 when game is active"
        );
        assert_eq!(prof_id, 10);
        assert_eq!(pid, 5555);

        // Poll GameStarted event
        assert_eq!(
            predator_game_profile_poll_event(&mut ev_type, &mut prof_id, &mut pid),
            1,
            "Poll must return 1 when event is available"
        );
        assert_eq!(ev_type, 1, "Event type must be GameStarted (1)");
        assert_eq!(prof_id, 10);
        assert_eq!(pid, 5555);

        // Simulate GameSwitched (Elden Ring pid 7777)
        game_profile::with_engine(|engine| {
            engine.handle_foreground_transition(7777, "eldenring.exe", "", 10_500);
        });

        assert_eq!(
            predator_game_profile_poll_event(&mut ev_type, &mut prof_id, &mut pid),
            1
        );
        assert_eq!(ev_type, 3, "Event type must be GameSwitched (3)");
        assert_eq!(prof_id, 20, "prof_id must be new profile ID (20)");
        assert_eq!(pid, 10, "pid parameter carries old profile ID (10)");

        // Verify active game is now Elden Ring
        assert_eq!(
            predator_game_profile_get_active(&mut prof_id, &mut pid),
            1
        );
        assert_eq!(prof_id, 20);
        assert_eq!(pid, 7777);

        // Simulate GameExited (Elden Ring pid 7777 exits)
        game_profile::with_engine(|engine| {
            engine.handle_process_exit(7777);
        });

        assert_eq!(
            predator_game_profile_poll_event(&mut ev_type, &mut prof_id, &mut pid),
            1
        );
        assert_eq!(ev_type, 2, "Event type must be GameExited (2)");
        assert_eq!(prof_id, 20);
        assert_eq!(pid, 7777);

        // Active game is now None
        assert_eq!(
            predator_game_profile_get_active(&mut prof_id, &mut pid),
            0
        );

        // Queue is empty again
        assert_eq!(
            predator_game_profile_poll_event(&mut ev_type, &mut prof_id, &mut pid),
            0
        );

        assert_eq!(predator_game_profile_shutdown(), 0);
    }
}

#[test]
fn test_game_profile_c_abi_multithreaded_hammering() {
    let _lock = PROFILE_TEST_MUTEX.lock().unwrap_or_else(|e| e.into_inner());

    unsafe {
        assert_eq!(predator_game_profile_init(), 0);
        assert_eq!(predator_game_profile_clear(), 0);
    }

    let running = Arc::new(std::sync::atomic::AtomicBool::new(true));
    let mut handles = Vec::new();

    // 4 Writer/Registrar threads
    for t_idx in 0..4 {
        let r = Arc::clone(&running);
        let h = thread::spawn(move || {
            let mut iter = 0;
            while r.load(std::sync::atomic::Ordering::Relaxed) && iter < 100 {
                let id = (t_idx * 100 + iter % 50 + 1) as u32;
                let name = format!("game_{}_{}.exe\0", t_idx, iter % 50);
                unsafe {
                    predator_game_profile_register(
                        id,
                        name.as_ptr(),
                        std::ptr::null(),
                        1,
                        50,
                        100,
                        0,
                        60,
                        0,
                    );
                    if iter % 5 == 0 {
                        predator_game_profile_unregister(id);
                    }
                }
                iter += 1;
            }
        });
        handles.push(h);
    }

    // 4 Poller/Observer threads
    for _ in 0..4 {
        let r = Arc::clone(&running);
        let h = thread::spawn(move || {
            let mut iter = 0;
            while r.load(std::sync::atomic::Ordering::Relaxed) && iter < 100 {
                let mut ev_type = 0;
                let mut prof_id = 0;
                let mut pid = 0;
                unsafe {
                    let _ = predator_game_profile_poll_event(&mut ev_type, &mut prof_id, &mut pid);
                    let _ = predator_game_profile_get_active(&mut prof_id, &mut pid);
                }
                iter += 1;
            }
        });
        handles.push(h);
    }

    for h in handles {
        h.join().unwrap();
    }

    unsafe {
        assert_eq!(predator_game_profile_shutdown(), 0);
    }
}
